// SPDX-FileCopyrightText: 2026 BeanPaste233
// SPDX-License-Identifier: LGPL-3.0-or-later

using System;
using System.Threading;
using System.Threading.Tasks;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;
using Xunit;

namespace LibUsbDotNet.Tests;

/// <summary>不依赖实体设备的等待、取消、抖动与引用所有权回归。</summary>
public class DeviceManagerTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task SameFinderSupportsIndependentConcurrentWaits()
    {
        using var api = new HotplugTests.FakeContextApi();
        using var context = new UsbContext(api);
        using var manager = new DeviceManager(context);
        manager.Start();
        var finder = new UsbDeviceFinder();
        var first = manager.WaitForDeviceArrival(finder, Limit, default);
        var second = manager.WaitForDeviceArrival(finder, Limit, default);
        api.Emit(1, HotplugEvent.DeviceArrived);
        using var a = await first.WaitAsync(Limit);
        using var b = await second.WaitAsync(Limit);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotSame(a, b);
        a.Dispose();
        b.Close(); // b 仍未释放；此时未打开，不会进入原生 I/O。
        api.Emit(1, HotplugEvent.DeviceLeft);
        Assert.Empty(context.DeviceInfoDictionary);
    }

    [Fact]
    public async Task CancelOneWaitDoesNotCancelAnotherWithSameFinder()
    {
        using var api = new HotplugTests.FakeContextApi();
        using var context = new UsbContext(api);
        using var manager = new DeviceManager(context);
        manager.Start();
        using var canceled = new CancellationTokenSource();
        var finder = new UsbDeviceFinder();
        var first = manager.WaitForDeviceArrival(finder, Limit, canceled.Token);
        var second = manager.WaitForDeviceArrival(finder, Limit, default);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        api.Emit(1, HotplugEvent.DeviceArrived);
        using var result = await second.WaitAsync(Limit);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task TimeoutDoesNotPoisonNextWait()
    {
        using var api = new HotplugTests.FakeContextApi();
        using var context = new UsbContext(api);
        using var manager = new DeviceManager(context);
        manager.Start();
        var finder = new UsbDeviceFinder();
        Assert.Null(await manager.WaitForDeviceArrival(finder, TimeSpan.FromMilliseconds(20), default));
        var next = manager.WaitForDeviceArrival(finder, Limit, default);
        api.Emit(1, HotplugEvent.DeviceArrived);
        using var result = await next.WaitAsync(Limit);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task PreCanceledWaitRejectsEvenAlreadyPresentDevice()
    {
        using var api = new HotplugTests.FakeContextApi();
        using var context = new UsbContext(api);
        using var manager = new DeviceManager(context);
        manager.Start();
        api.Emit(1, HotplugEvent.DeviceArrived);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.WaitForDeviceArrival(new UsbDeviceFinder(), Limit, new CancellationToken(true)));
    }

    [Fact]
    public async Task StopCancelsPendingWaitAndAllowsRestart()
    {
        using var api = new HotplugTests.FakeContextApi();
        using var context = new UsbContext(api);
        using var manager = new DeviceManager(context);
        manager.Start();
        var pending = manager.WaitForDeviceArrival(new UsbDeviceFinder(), Timeout.InfiniteTimeSpan, default);
        manager.Stop();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Limit));
        Assert.True(context.IsUsingHotplug);
        manager.Start();
        var next = manager.WaitForDeviceArrival(new UsbDeviceFinder(), Limit, default);
        api.Emit(1, HotplugEvent.DeviceArrived);
        using var result = await next.WaitAsync(Limit);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task WaitForNewArrivalRequiresNewAttachment()
    {
        using var api = new HotplugTests.FakeContextApi();
        using var context = new UsbContext(api);
        using var manager = new DeviceManager(context);
        manager.Start();
        api.Emit(1, HotplugEvent.DeviceArrived);
        var pending = manager.WaitForNewDeviceArrival(new UsbDeviceFinder(), Limit, default);
        Assert.False(pending.IsCompleted);
        api.Emit(1, HotplugEvent.DeviceArrived); // 重复事件不算重连。
        Assert.False(pending.IsCompleted);
        api.Emit(1, HotplugEvent.DeviceLeft);
        api.Emit(1, HotplugEvent.DeviceArrived);
        using var result = await pending.WaitAsync(Limit);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ExistingDeviceMustAlsoMeetStabilityInterval()
    {
        using var api = new HotplugTests.FakeContextApi();
        using var context = new UsbContext(api);
        using var manager = new DeviceManager(context);
        manager.Start();
        api.Emit(1, HotplugEvent.DeviceArrived);
        var pending = manager.WaitForDeviceArrival(new UsbDeviceFinder(), Limit, default, TimeSpan.FromMilliseconds(80));
        Assert.False(pending.IsCompleted);
        using var result = await pending.WaitAsync(Limit);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task UnstableDeviceIsNotReturnedAtDeadline()
    {
        using var api = new HotplugTests.FakeContextApi();
        using var context = new UsbContext(api);
        using var manager = new DeviceManager(context);
        manager.Start();
        var pending = manager.WaitForDeviceArrival(new UsbDeviceFinder(), TimeSpan.FromMilliseconds(100), default, TimeSpan.FromMilliseconds(200));
        api.Emit(1, HotplugEvent.DeviceArrived);
        api.Emit(1, HotplugEvent.DeviceLeft);
        Assert.Null(await pending.WaitAsync(Limit));
    }

    [Fact]
    public async Task UnregisterCancelsPendingWaitEvenIfImmediatelyRegisteredAgain()
    {
        using var api = new HotplugTests.FakeContextApi();
        using var context = new UsbContext(api);
        using var manager = new DeviceManager(context);
        manager.Start();
        var pending = manager.WaitForDeviceArrival(new UsbDeviceFinder(), Timeout.InfiniteTimeSpan, default);
        context.UnregisterHotPlug();
        context.RegisterHotPlug();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Limit));
    }

    [Fact]
    public async Task WaitDoesNotDependOnPublicSubscriberFinishing()
    {
        using var api = new HotplugTests.FakeContextApi();
        using var context = new UsbContext(api);
        using var manager = new DeviceManager(context);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        context.DeviceEvent += (_, _) => { entered.Set(); release.Wait(); };
        manager.Start();
        var pending = manager.WaitForDeviceArrival(new UsbDeviceFinder(), Limit, default);
        api.Emit(1, HotplugEvent.DeviceArrived);
        try
        {
            Assert.True(entered.Wait(Limit));
            using var result = await pending.WaitAsync(Limit);
            Assert.NotNull(result);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task ZeroTimeoutReturnsPresentDeviceWithoutDelay()
    {
        using var api = new HotplugTests.FakeContextApi();
        using var context = new UsbContext(api);
        using var manager = new DeviceManager(context);
        manager.Start();
        api.Emit(1, HotplugEvent.DeviceArrived);
        using var result = await manager.WaitForDeviceArrival(new UsbDeviceFinder(), TimeSpan.Zero, default);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task CustomDeviceFinderOverrideIsHonored()
    {
        using var api = new HotplugTests.FakeContextApi();
        using var context = new UsbContext(api);
        using var manager = new DeviceManager(context);
        manager.Start();
        var finder = new CustomFinder();
        api.Emit(1, HotplugEvent.DeviceArrived);
        using var result = await manager.WaitForDeviceArrival(finder, TimeSpan.Zero, default);
        Assert.NotNull(result);
        Assert.True(finder.Called);
    }

    private sealed class CustomFinder : UsbDeviceFinder
    {
        internal bool Called;
        public override bool Check(IUsbDevice device) { Called = true; return true; }
    }
}
