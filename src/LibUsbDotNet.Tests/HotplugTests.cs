// SPDX-FileCopyrightText: 2026 BeanPaste233
// SPDX-License-Identifier: LGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using LibUsbDotNet.Info;
using LibUsbDotNet.LibUsb;
using Xunit;

namespace LibUsbDotNet.Tests;

/// <summary>使用模拟原生事件，不向实际 USB 设备提交 I/O。</summary>
public class HotplugTests
{
    [Fact]
    public void RegistrationFailureDoesNotActivateHotplug()
    {
        using var api = new FakeContextApi { RegisterError = Error.Io };
        using var context = new UsbContext(api);
        Assert.Throws<UsbException>(context.RegisterHotPlug);
        Assert.False(context.IsUsingHotplug);
        Assert.Equal(0, api.HandleCount);
    }

    [Fact]
    public void UnknownDepartureDoesNotThrow()
    {
        using var api = new FakeContextApi();
        using var context = new UsbContext(api);
        context.RegisterHotPlug();
        api.Emit(1, HotplugEvent.DeviceLeft);
        Assert.Empty(context.DeviceInfoDictionary);
    }

    [Fact]
    public async Task DuplicateArrivalIsDeliveredOnce()
    {
        using var api = new FakeContextApi();
        using var context = new UsbContext(api);
        int count = 0;
        var left = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.DeviceEvent += (_, e) =>
        {
            if (e is DeviceArrivedEventArgs) Interlocked.Increment(ref count);
            if (e is DeviceLeftEventArgs) left.TrySetResult(true);
        };
        context.RegisterHotPlug();
        api.Emit(1, HotplugEvent.DeviceArrived);
        api.Emit(1, HotplugEvent.DeviceArrived);
        api.Emit(1, HotplugEvent.DeviceLeft);
        await left.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ThrowingSubscriberDoesNotEscapeOrSuppressNextSubscriber()
    {
        using var api = new FakeContextApi();
        using var context = new UsbContext(api);
        var delivered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.DeviceEvent += (_, _) => throw new InvalidOperationException("Subscriber failure");
        context.DeviceEvent += (_, _) => delivered.TrySetResult(true);
        context.RegisterHotPlug();
        api.Emit(1, HotplugEvent.DeviceArrived);
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task SlowSubscriberDoesNotBlockNativeCallback()
    {
        using var api = new FakeContextApi();
        using var context = new UsbContext(api);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        context.DeviceEvent += (_, _) => { entered.Set(); release.Wait(); };
        context.RegisterHotPlug();
        var callback = Task.Run(() => api.Emit(1, HotplugEvent.DeviceArrived));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            Assert.Same(callback, await Task.WhenAny(callback, Task.Delay(300)));
        }
        finally
        {
            release.Set();
            await callback.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public void UnregisterKeepsEventLoopForOpenDevices()
    {
        using var api = new FakeContextApi();
        using var context = new UsbContext(api);
        using var device = api.RetainDevice((IntPtr)1, context);
        context.OpenDevices.Add(device);
        context.RegisterHotPlug();
        context.UnregisterHotPlug();
        Assert.Equal(0, context.stopHandlingEvents);
        context.OpenDevices.Clear();
    }

    [Fact]
    public async Task StopHandlingEventsWakesIdleLoop()
    {
        using var api = new FakeContextApi();
        using var context = new UsbContext(api);
        context.StartHandlingEvents();
        var stop = Task.Run(context.StopHandlingEvents);
        try
        {
            Assert.Same(stop, await Task.WhenAny(stop, Task.Delay(300)));
            Assert.True(api.InterruptCount > 0);
        }
        finally
        {
            Interlocked.Exchange(ref context.stopHandlingEvents, 1);
            api.Wake();
            await stop.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public async Task NativeRegistrationEnumerationCanBeSynchronous()
    {
        using var api = new FakeContextApi { DeviceIds = new[] { 1 } };
        using var context = new UsbContext(api);
        using var manager = new DeviceManager(context);
        manager.Start();
        using var result = await manager.WaitForDeviceArrival(new Main.UsbDeviceFinder(), TimeSpan.Zero, default);
        Assert.NotNull(result);
        Assert.Single(context.DeviceInfoDictionary);
    }

    [Fact]
    public void FailedRegistrationRollsBackEnumeratedDeviceReferences()
    {
        using var api = new FakeContextApi { DeviceIds = new[] { 1 }, RegisterError = Error.Io };
        var context = new UsbContext(api);
        Assert.Throws<UsbException>(context.RegisterHotPlug);
        context.Dispose();
        Assert.Empty(context.DeviceInfoDictionary);
        Assert.Equal(api.RetainedCount, api.ReleasedCount);
    }

    [Fact]
    public async Task PollingFallbackDetectsRemovalAndReconnection()
    {
        using var api = new FakeContextApi { SupportsHotplug = false, DeviceIds = new[] { 1 } };
        using var context = new UsbContext(api);
        using var manager = new DeviceManager(context);
        var left = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.DeviceEvent += (_, e) => { if (e is DeviceLeftEventArgs) left.TrySetResult(true); };
        manager.Start();
        Assert.Single(context.DeviceInfoDictionary);
        var next = manager.WaitForNewDeviceArrival(new Main.UsbDeviceFinder(), TimeSpan.FromSeconds(3), default);
        Volatile.Write(ref api.DeviceIds, Array.Empty<int>());
        await left.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Volatile.Write(ref api.DeviceIds, new[] { 1 });
        using var result = await next.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotNull(result);
    }

    [Fact]
    public async Task PollingEnumerationFailureDoesNotReportFalseRemoval()
    {
        using var api = new FakeContextApi { SupportsHotplug = false, DeviceIds = new[] { 1 } };
        using var context = new UsbContext(api);
        var error = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int removals = 0;
        context.DeviceEvent += (_, e) => { if (e is DeviceLeftEventArgs) Interlocked.Increment(ref removals); };
        context.DeviceEventError += (_, _) => error.TrySetResult(true);
        context.RegisterHotPlug();
        api.FailList = true;
        await error.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Single(context.DeviceInfoDictionary);
        Assert.Equal(0, removals);
    }

    [Fact]
    public void PollingUsesDescriptorFilters()
    {
        using var api = new FakeContextApi { SupportsHotplug = false, DeviceIds = new[] { 1, 2 } };
        using var context = new UsbContext(api) { HotplugOptions = new HotplugOptions { ProductId = 1 } };
        context.RegisterHotPlug();
        Assert.Single(context.DeviceInfoDictionary);
    }

    [Fact]
    public void ArrivalOnlyNotificationsStillRemoveDepartedDevicesFromCache()
    {
        using var api = new FakeContextApi();
        using var context = new UsbContext(api)
        {
            HotplugOptions = new HotplugOptions { HotplugEventFlags = HotplugEvent.DeviceArrived }
        };
        context.RegisterHotPlug();
        api.Emit(1, HotplugEvent.DeviceArrived);
        api.Emit(1, HotplugEvent.DeviceLeft);
        Assert.Empty(context.DeviceInfoDictionary);
    }

    [Fact]
    public void RepeatedEventsReleaseEveryOwnedReference()
    {
        using var api = new FakeContextApi();
        var context = new UsbContext(api);
        context.RegisterHotPlug();
        for (int i = 0; i < 1000; i++)
        {
            api.Emit(1, HotplugEvent.DeviceArrived);
            api.Emit(1, HotplugEvent.DeviceArrived);
            api.Emit(1, HotplugEvent.DeviceLeft);
            api.Emit(1, HotplugEvent.DeviceLeft);
        }
        context.Dispose();
        Assert.Equal(api.RetainedCount, api.ReleasedCount);
        Assert.Empty(context.DeviceInfoDictionary);
    }

    [Fact]
    public async Task SubscriberCanUnregisterWithoutJoiningItself()
    {
        using var api = new FakeContextApi();
        using var context = new UsbContext(api);
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.DeviceEvent += (_, _) => { context.UnregisterHotPlug(); done.TrySetResult(true); };
        context.RegisterHotPlug();
        api.Emit(1, HotplugEvent.DeviceArrived);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(context.IsUsingHotplug);
    }

    [Fact]
    public async Task SubscriberCanDisposeContextWithoutJoiningItself()
    {
        using var api = new FakeContextApi();
        using var context = new UsbContext(api);
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.DeviceEvent += (_, _) => { context.Dispose(); done.TrySetResult(true); };
        context.RegisterHotPlug();
        api.Emit(1, HotplugEvent.DeviceArrived);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(context.IsDisposed);
    }

    [Fact]
    public void DistinctNativePointersWithSameHashAreNotEqual()
    {
        if (IntPtr.Size != 8) return;
        using var api = new FakeContextApi();
        using var context = new UsbContext(api);
        using var first = api.RetainDevice((IntPtr)1, context);
        using var second = api.RetainDevice(new IntPtr(0x100000000L), context);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task NotificationOverflowIsReportedWithoutLosingCachedStateOrReferences()
    {
        using var api = new FakeContextApi();
        using var context = new UsbContext(api);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var overflow = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int first = 0;
        context.DeviceEvent += (_, _) =>
        {
            if (Interlocked.Increment(ref first) == 1) { entered.Set(); release.Wait(); }
        };
        context.DeviceEventError += (_, e) =>
        {
            if (((Exception)e.ExceptionObject).Message.Contains("queue overflow")) overflow.TrySetResult(true);
        };
        context.RegisterHotPlug();
        api.Emit(1, HotplugEvent.DeviceArrived);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            for (int i = 2; i <= 400; i++) api.Emit(i, HotplugEvent.DeviceArrived);
            Assert.Equal(400, context.DeviceInfoDictionary.Count);
        }
        finally { release.Set(); }
        await overflow.Task.WaitAsync(TimeSpan.FromSeconds(3));
        context.Dispose();
        Assert.Equal(api.RetainedCount, api.ReleasedCount);
    }

    [Fact]
    public async Task NativeEventFailureCancelsWaitWithoutWaitingForUserNotifications()
    {
        using var api = new FakeContextApi();
        using var context = new UsbContext(api);
        using var manager = new DeviceManager(context);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        context.DeviceEvent += (_, _) => { entered.Set(); release.Wait(); };
        manager.Start();
        api.Emit(1, HotplugEvent.DeviceArrived);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            var waiting = manager.WaitForNewDeviceArrival(new Main.UsbDeviceFinder(), Timeout.InfiniteTimeSpan, default);
            api.EventError = Error.Io;
            api.Wake();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task NativeEventThreadCannotJoinItself()
    {
        using var api = new FakeContextApi();
        using var context = new UsbContext(api);
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.OnHandle = () =>
        {
            try { Assert.Throws<InvalidOperationException>(context.StopHandlingEvents); done.TrySetResult(true); }
            catch (Exception ex) { done.TrySetException(ex); }
        };
        context.StartHandlingEvents();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InvalidOptionsAreRejectedOnBothBackends(bool native)
    {
        using var api = new FakeContextApi { SupportsHotplug = native };
        using var context = new UsbContext(api) { HotplugOptions = new HotplugOptions { VendorId = 65536 } };
        Assert.Throws<ArgumentOutOfRangeException>(context.RegisterHotPlug);
        Assert.False(context.IsUsingHotplug);
    }

    internal sealed class FakeContextApi : IUsbContextApi, IDisposable
    {
        private readonly AutoResetEvent wake = new(false);
        private HotplugCallbackFn callback;
        public bool SupportsHotplug { get; set; } = true;
        public Error RegisterError { get; set; }
        public int HandleCount;
        public int InterruptCount;
        public Error EventError;
        public Action OnHandle;
        public int RetainedCount;
        public int ReleasedCount;
        public int[] DeviceIds = Array.Empty<int>();
        public volatile bool FailList;
        public Func<DeviceHandle> DeviceHandleFactory;

        public Error Open(Device device, out DeviceHandle handle)
        {
            handle = DeviceHandleFactory?.Invoke() ?? throw new InvalidOperationException("No fake open handle configured.");
            return Error.Success;
        }

        public Error Register(Context context, HotplugOptions options, IntPtr callbackPointer, ref int handle)
        {
            callback = Marshal.GetDelegateForFunctionPointer<HotplugCallbackFn>(callbackPointer);
            foreach (int id in Volatile.Read(ref DeviceIds)) Emit(id, HotplugEvent.DeviceArrived);
            handle = 7;
            return RegisterError;
        }

        public void Deregister(Context context, int handle) { }

        public Error HandleEvents(Context context, ref int completed)
        {
            Interlocked.Increment(ref HandleCount);
            Interlocked.Exchange(ref OnHandle, null)?.Invoke();
            wake.WaitOne(10);
            return EventError;
        }

        public void Interrupt(Context context) { Interlocked.Increment(ref InterruptCount); wake.Set(); }
        public void Wake() => wake.Set();
        public void Emit(int id, HotplugEvent type) => callback(IntPtr.Zero, (IntPtr)id, type, IntPtr.Zero);
        public UsbDevice RetainDevice(IntPtr device, UsbContext context)
        {
            Interlocked.Increment(ref RetainedCount);
            return new UsbDevice(new TrackingDevice(device, this), context);
        }
        public CachedDeviceInfo GetDeviceInfo(UsbDevice device) => new(new FakeDeviceInfo((ushort)device.GetHashCode()), LocationId.Zero, device.GetHashCode());
        public UsbDeviceCollection List(UsbContext context)
        {
            if (FailList) throw new InvalidOperationException("Simulated enumeration failure");
            var devices = new List<IUsbDevice>();
            foreach (int id in Volatile.Read(ref DeviceIds)) devices.Add(RetainDevice((IntPtr)id, context));
            return new UsbDeviceCollection(devices);
        }
        public void Dispose() => wake.Dispose();

        private sealed class TrackingDevice : Device
        {
            private readonly FakeContextApi api;
            internal TrackingDevice(IntPtr value, FakeContextApi api) { this.api = api; SetHandle(value); }
            protected override bool ReleaseHandle() { Interlocked.Increment(ref api.ReleasedCount); return true; }
        }
    }

    private sealed class FakeDeviceInfo : UsbDeviceInfo
    {
        internal FakeDeviceInfo(ushort productId) { ProductId = productId; }
    }
}
