// SPDX-FileCopyrightText: 2026 BeanPaste233
// SPDX-License-Identifier: LGPL-3.0-or-later

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;
using Xunit;

namespace LibUsbDotNet.Tests;

/// <summary>Exercises native failure paths without submitting I/O to a USB device.</summary>
public class AsyncTransferTests
{
    [Fact]
    public void AbandonedIdlePoolDoesNotRootTheDeviceOrLeakNativeStorage()
    {
        using var api = new FakeTransferApi();
        var weak = CompleteAndForgetDevice(api);
        for (int i = 0; i < 10 && api.FreeCount == 0; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(weak.IsAlive);
        Assert.Equal(1, api.AllocateCount);
        Assert.Equal(1, api.FreeCount);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CompleteAndForgetDevice(FakeTransferApi api)
    {
        var device = new TrackingDeviceHandle();
        var task = Start(device, new byte[8], api);
        api.Complete(TransferStatus.Completed, 8);
        task.GetAwaiter().GetResult();
        device.Dispose(); // 故意绕过UsbDevice.Close，验证异常遗失池的最终回收兜底。
        return new WeakReference(device);
    }

    [Fact]
    public async Task SuccessfulTransfersReuseNativeStorageButKeepIndependentTasksAndCancellation()
    {
        using var api = new FakeTransferApi();
        using var device = new TrackingDeviceHandle();
        using var firstCancellation = new CancellationTokenSource();
        var first = Start(device, new byte[8], api, firstCancellation.Token);
        api.Complete(TransferStatus.Completed, 3);
        Assert.Equal(3, (await first).transferLength);
        var second = Start(device, new byte[16], api);
        firstCancellation.Cancel();
        Assert.False(second.IsCompleted);
        Assert.Equal(0, api.CancelCount);
        api.Complete(TransferStatus.Completed, 11);
        Assert.Equal(11, (await second).transferLength);
        Assert.Equal(3, (await first).transferLength);
        Assert.Equal(1, api.AllocateCount);
        Assert.Equal(0, api.FreeCount);
        await AsyncTransfer.CancelAndDrainAsync(device);
        Assert.Equal(1, api.FreeCount);
    }

    [Fact]
    public async Task ReusedTransferSubmitFailureReleasesPoolEntryAndPins()
    {
        using var api = new FakeTransferApi();
        using var device = new TrackingDeviceHandle();
        using var buffer = new TrackingMemory();
        var first = Start(device, buffer.Memory, api);
        api.Complete(TransferStatus.Completed, 8);
        await first;
        api.SubmitError = Error.Io;
        await Assert.ThrowsAsync<UsbException>(() => Start(device, buffer.Memory, api));
        Assert.Equal(1, api.AllocateCount);
        Assert.Equal(1, api.FreeCount);
        Assert.Equal(0, buffer.PinCount);
        await AsyncTransfer.CancelAndDrainAsync(device);
        Assert.Equal(1, api.FreeCount);
    }

    [Fact]
    public async Task IdlePoolIsBoundedAndSeparatedByDeviceApiAndIsoCapacity()
    {
        using var device = new TrackingDeviceHandle();
        using var other = new TrackingDeviceHandle();
        var apis = new List<FakeTransferApi>();
        try
        {
            for (int i = 0; i < 17; i++)
            {
                var api = new FakeTransferApi();
                apis.Add(api);
                var task = Start(device, new byte[8], api);
                api.Complete(TransferStatus.Completed, 8);
                await task;
                Assert.Equal(i == 16 ? 1 : 0, api.FreeCount);
            }
            var first = apis[0];
            var differentDevice = Start(other, new byte[8], first);
            first.Complete(TransferStatus.Completed, 8);
            await differentDevice;
            Assert.Equal(2, first.AllocateCount);
            var iso = Start(device, new byte[8], first, isoPacketSize: 4);
            first.Complete(TransferStatus.Completed, 8);
            await iso;
            Assert.Equal(3, first.AllocateCount);
            Assert.Equal(1, first.FreeCount); // 原池满；ISO容量不同不能借用已有bulk存储。
            var bulk = Start(device, new byte[8], first);
            first.Complete(TransferStatus.Completed, 8);
            await bulk;
            Assert.Equal(3, first.AllocateCount);
            await AsyncTransfer.CancelAndDrainAsync(device);
            Assert.Equal(2, first.FreeCount);
            await AsyncTransfer.CancelAndDrainAsync(other);
            Assert.Equal(3, first.FreeCount);
            Assert.All(apis, api => Assert.Equal(api.AllocateCount, api.FreeCount));
        }
        finally
        {
            await AsyncTransfer.CancelAndDrainAsync(device);
            await AsyncTransfer.CancelAndDrainAsync(other);
            foreach (var api in apis) api.Dispose();
        }
    }

    [Fact]
    public async Task RepeatedReuseDoesNotRetainPriorCancellationOrAllocateNativeTransfers()
    {
        using var api = new FakeTransferApi();
        using var device = new TrackingDeviceHandle();
        for (int i = 0; i < 250; i++)
        {
            using var cancellation = new CancellationTokenSource();
            var task = Start(device, new byte[8], api, cancellation.Token);
            var cancel = Task.Run(cancellation.Cancel);
            api.Complete(TransferStatus.Completed, i);
            await cancel;
            Assert.Equal(i, (await task).transferLength);
        }
        Assert.Equal(1, api.AllocateCount);
        Assert.Equal(0, api.FreeCount);
        await AsyncTransfer.CancelAndDrainAsync(device);
        Assert.Equal(1, api.FreeCount);
    }

    [Fact]
    public async Task SubmitFailureReleasesAllResources()
    {
        using var api = new FakeTransferApi { SubmitError = Error.Io };
        using var device = new TrackingDeviceHandle();
        using var buffer = new TrackingMemory();
        int pendingBefore = AsyncTransfer.PendingCount;

        await Assert.ThrowsAsync<UsbException>(() => Start(device, buffer.Memory, api));

        Assert.Equal(0, buffer.PinCount);
        Assert.Equal(1, api.FreeCount);
        Assert.Equal(pendingBefore, AsyncTransfer.PendingCount);
    }

    [Fact]
    public async Task SubmissionExceptionReleasesAllResources()
    {
        using var api = new FakeTransferApi { ThrowOnSubmit = true };
        using var device = new TrackingDeviceHandle();
        using var buffer = new TrackingMemory();
        int pendingBefore = AsyncTransfer.PendingCount;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Start(device, buffer.Memory, api));

        Assert.Equal(0, buffer.PinCount);
        Assert.Equal(1, api.FreeCount);
        Assert.Equal(pendingBefore, AsyncTransfer.PendingCount);
    }

    [Fact]
    public async Task AllocationFailureDoesNotDereferenceNullOrRetainPin()
    {
        using var api = new FakeTransferApi { ReturnNull = true };
        using var device = new TrackingDeviceHandle();
        using var buffer = new TrackingMemory();
        int pendingBefore = AsyncTransfer.PendingCount;

        await Assert.ThrowsAsync<OutOfMemoryException>(() => Start(device, buffer.Memory, api));

        Assert.Equal(0, buffer.PinCount);
        Assert.Equal(0, api.FreeCount);
        Assert.Equal(pendingBefore, AsyncTransfer.PendingCount);
    }

    [Fact]
    public async Task PinFailureDoesNotAllocateOrRetainDeviceReference()
    {
        using var api = new FakeTransferApi();
        using var device = new TrackingDeviceHandle();
        using var buffer = new TrackingMemory { ThrowOnPin = true };
        int pendingBefore = AsyncTransfer.PendingCount;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Start(device, buffer.Memory, api));
        device.Dispose();

        Assert.Equal(1, device.ReleaseCount);
        Assert.Equal(0, buffer.PinCount);
        Assert.Equal(0, api.AllocateCount);
        Assert.Equal(pendingBefore, AsyncTransfer.PendingCount);
    }

    [Theory]
    [InlineData(TransferStatus.Completed, Error.Success)]
    [InlineData(TransferStatus.TimedOut, Error.Timeout)]
    [InlineData(TransferStatus.Stall, Error.Pipe)]
    [InlineData(TransferStatus.Overflow, Error.Overflow)]
    [InlineData(TransferStatus.NoDevice, Error.NoDevice)]
    [InlineData(TransferStatus.Cancelled, Error.Io)]
    [InlineData(TransferStatus.Error, Error.Io)]
    public async Task CompletionReturnsStatusAndReleasesResources(TransferStatus status, Error expected)
    {
        using var api = new FakeTransferApi();
        using var device = new TrackingDeviceHandle();
        using var buffer = new TrackingMemory();
        int pendingBefore = AsyncTransfer.PendingCount;
        var task = Start(device, buffer.Memory, api);
        bool taskCompletedBeforeUnpin = false;
        buffer.OnUnpin = () => taskCompletedBeforeUnpin = task.IsCompleted;

        api.Complete(status, 7);
        var result = await task;

        Assert.Equal(expected, result.error);
        Assert.Equal(7, result.transferLength);
        Assert.False(taskCompletedBeforeUnpin);
        Assert.Equal(0, buffer.PinCount);
        await AsyncTransfer.CancelAndDrainAsync(device);
        Assert.Equal(1, api.FreeCount);
        Assert.Equal(pendingBefore, AsyncTransfer.PendingCount);
    }

    [Fact]
    public async Task SubmittedTransferKeepsDeviceAliveUntilCompletion()
    {
        using var api = new FakeTransferApi();
        using var device = new TrackingDeviceHandle();
        using var buffer = new TrackingMemory();
        var task = Start(device, buffer.Memory, api);
        device.Dispose();
        int releasedBeforeCompletion = device.ReleaseCount;

        api.Complete(TransferStatus.Completed, 0);
        await task;

        Assert.Equal(0, releasedBeforeCompletion);
        Assert.Equal(1, device.ReleaseCount);
        Assert.Equal(0, buffer.PinCount);
    }

    [Fact]
    public async Task DisposedDeviceIsRejectedBeforePinning()
    {
        using var api = new FakeTransferApi();
        using var device = new TrackingDeviceHandle();
        using var buffer = new TrackingMemory();
        device.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => Start(device, buffer.Memory, api));
        Assert.Equal(0, buffer.PinCount);
        Assert.Equal(0, api.AllocateCount);
    }

    [Fact]
    public async Task PreCancelledRequestDoesNotPinOrAllocate()
    {
        using var api = new FakeTransferApi();
        using var device = new TrackingDeviceHandle();
        using var buffer = new TrackingMemory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var task = Start(device, buffer.Memory, api, cancellation.Token);
        bool completedBeforeCallback = task.IsCompleted;
        if (!completedBeforeCallback) api.Complete(TransferStatus.Cancelled, 0);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(completedBeforeCallback);
        Assert.Equal(0, api.AllocateCount);
        Assert.Equal(0, buffer.PinCount);
    }

    [Fact]
    public async Task CancellationWaitsForNativeCallbackBeforeUnpinning()
    {
        using var api = new FakeTransferApi();
        using var device = new TrackingDeviceHandle();
        using var buffer = new TrackingMemory();
        using var cancellation = new CancellationTokenSource();
        var task = Start(device, buffer.Memory, api, cancellation.Token);
        cancellation.Cancel();
        bool completedEarly = task.IsCompleted;
        int pinsBeforeCallback = buffer.PinCount;
        int freedBeforeCallback = api.FreeCount;
        api.Complete(TransferStatus.Cancelled, 3);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(completedEarly);
        Assert.Equal(1, pinsBeforeCallback);
        Assert.Equal(0, freedBeforeCallback);
        Assert.Equal(1, api.CancelCount);
        Assert.Equal(0, buffer.PinCount);
        Assert.Equal(1, api.FreeCount);
    }

    [Theory]
    [InlineData(Error.Success)]
    [InlineData(Error.NotFound)]
    [InlineData(Error.Io)]
    public async Task CloseDrainsFinalCallbackEvenWhenCancelFails(Error cancelError)
    {
        using var api = new FakeTransferApi { CancelError = cancelError };
        using var device = new TrackingDeviceHandle();
        using var buffer = new TrackingMemory();
        var transfer = Start(device, buffer.Memory, api);
        var closing = AsyncTransfer.CancelAndDrainAsync(device);
        bool closedBeforeCallback = closing.IsCompleted;
        device.Dispose();
        int releasedBeforeCallback = device.ReleaseCount;
        api.Complete(TransferStatus.Cancelled, 0);
        await closing.WaitAsync(TimeSpan.FromSeconds(2));
        await transfer;

        Assert.False(closedBeforeCallback);
        Assert.Equal(1, api.CancelCount);
        Assert.Equal(0, releasedBeforeCallback);
        Assert.Equal(1, device.ReleaseCount);
        Assert.Equal(0, buffer.PinCount);
    }

    [Fact]
    public async Task CloseRejectsSubmissionsAndDoesNotAffectOtherDevices()
    {
        using var api = new FakeTransferApi();
        using var otherApi = new FakeTransferApi();
        using var device = new TrackingDeviceHandle();
        using var otherDevice = new TrackingDeviceHandle();
        using var buffer = new TrackingMemory();
        await AsyncTransfer.CancelAndDrainAsync(device);
        var rejected = Start(device, buffer.Memory, api);
        bool rejectedEarly = rejected.IsCompleted;
        if (!rejectedEarly) api.Complete(TransferStatus.Completed, 0);
        var otherTransfer = Start(otherDevice, new byte[8], otherApi);
        otherApi.Complete(TransferStatus.Completed, 2);
        Assert.Equal(2, (await otherTransfer).transferLength);
        await AsyncTransfer.CancelAndDrainAsync(otherDevice);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => rejected);
        Assert.Equal(0, api.AllocateCount);
    }

    [Fact]
    public async Task CloseWaitsForAllTransfersAndIsIdempotent()
    {
        using var firstApi = new FakeTransferApi();
        using var secondApi = new FakeTransferApi();
        using var device = new TrackingDeviceHandle();
        var first = Start(device, new byte[8], firstApi);
        var second = Start(device, new byte[8], secondApi);
        var closing = AsyncTransfer.CancelAndDrainAsync(device);
        var alsoClosing = AsyncTransfer.CancelAndDrainAsync(device);
        firstApi.Complete(TransferStatus.Cancelled, 0);
        bool closedWithOnePending = closing.IsCompleted || alsoClosing.IsCompleted;
        secondApi.Complete(TransferStatus.Cancelled, 0);
        await Task.WhenAll(first, second);
        await Task.WhenAll(closing, alsoClosing).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(closedWithOnePending);
        Assert.Equal(1, firstApi.CancelCount);
        Assert.Equal(1, secondApi.CancelCount);
    }

    [Fact]
    public async Task CancellationDuringSubmissionIsNotLost()
    {
        using var cancellation = new CancellationTokenSource();
        using var api = new FakeTransferApi { OnSubmit = cancellation.Cancel };
        using var device = new TrackingDeviceHandle();
        var task = Start(device, new byte[8], api, cancellation.Token);
        api.Complete(TransferStatus.Cancelled, 0);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, api.CancelCount);
    }

    [Fact]
    public async Task SuccessfulCompletionWinsRacingCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var api = new FakeTransferApi();
        using var device = new TrackingDeviceHandle();
        var task = Start(device, new byte[8], api, cancellation.Token);
        cancellation.Cancel();
        api.Complete(TransferStatus.Completed, 5);
        Assert.Equal(5, (await task).transferLength);
        cancellation.Cancel();
        await AsyncTransfer.CancelAndDrainAsync(device);
        Assert.Equal(1, api.CancelCount);
        Assert.Equal(1, api.FreeCount);
    }

    private static async Task<(Error error, int transferLength)> Start(
        DeviceHandle device, Memory<byte> buffer, IAsyncTransferApi api, CancellationToken cancellationToken = default, int isoPacketSize = 0)
    {
        Task<(Error error, int transferLength)> task;
        unsafe { task = AsyncTransfer.TransferAsync(device, 0x81, EndpointType.Bulk, buffer, 1000, isoPacketSize, nativeApi: api, cancellationToken: cancellationToken); }
        return await task;
    }

    [Fact]
    public async Task DeviceCloseKeepsEventLoopUntilFinalCallbackThenCanReopen()
    {
        using var native = new HotplugTests.FakeContextApi { DeviceHandleFactory = () => new TrackingDeviceHandle() };
        using var context = new UsbContext(native);
        using var device = native.RetainDevice((IntPtr)1, context);
        using var transferApi = new FakeTransferApi();
        device.Open();
        var handle = (TrackingDeviceHandle)device.DeviceHandle;
        var transfer = Start(handle, new byte[8], transferApi);
        var close = Task.Run(device.Close);
        bool requested = await WaitUntil(() => transferApi.CancelCount > 0 || close.IsCompleted);
        bool completedEarly = close.IsCompleted;
        bool stoppedEarly = Volatile.Read(ref context.stopHandlingEvents) != 0;
        int releasedEarly = handle.ReleaseCount;
        transferApi.Complete(TransferStatus.Cancelled, 0);
        await transfer;
        await close.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(requested);
        Assert.False(completedEarly);
        Assert.False(stoppedEarly);
        Assert.Equal(0, releasedEarly);
        Assert.Equal(1, handle.ReleaseCount);
        Assert.False(device.IsOpen);
        Assert.Empty(context.ReadOnlyOpenDevices);
        device.Open();
        Assert.True(device.IsOpen);
        device.Close();
    }

    [Fact]
    public async Task ContextDisposeDrainsTransfersBeforeStoppingEvents()
    {
        using var native = new HotplugTests.FakeContextApi { DeviceHandleFactory = () => new TrackingDeviceHandle() };
        using var context = new UsbContext(native);
        using var device = native.RetainDevice((IntPtr)1, context);
        using var transferApi = new FakeTransferApi();
        device.Open();
        var handle = (TrackingDeviceHandle)device.DeviceHandle;
        var transfer = Start(handle, new byte[8], transferApi);
        var close = Task.Run(context.Dispose);
        await WaitUntil(() => transferApi.CancelCount > 0 || close.IsCompleted);
        bool completedEarly = close.IsCompleted;
        bool stoppedEarly = Volatile.Read(ref context.stopHandlingEvents) != 0;
        transferApi.Complete(TransferStatus.Cancelled, 0);
        await transfer;
        await close.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(completedEarly);
        Assert.False(stoppedEarly);
        Assert.True(context.IsDisposed);
        Assert.Equal(1, handle.ReleaseCount);
    }

    [Fact]
    public async Task NativeEventThreadCannotCloseDeviceOrDisposeContext()
    {
        using var native = new HotplugTests.FakeContextApi { DeviceHandleFactory = () => new TrackingDeviceHandle() };
        using var context = new UsbContext(native);
        using var device = native.RetainDevice((IntPtr)1, context);
        device.Open();
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        native.OnHandle = () =>
        {
            try
            {
                Assert.Throws<InvalidOperationException>(device.Close);
                Assert.True(device.IsOpen);
                Assert.Throws<InvalidOperationException>(context.Dispose);
                Assert.False(context.IsDisposing);
                done.TrySetResult(true);
            }
            catch (Exception exception) { done.TrySetException(exception); }
        };
        native.Wake();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static async Task<bool> WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(5);
        return condition();
    }

    [Fact]
    public async Task CancellationExceptionDoesNotFreeSubmittedTransferEarly()
    {
        using var cancellation = new CancellationTokenSource();
        using var api = new FakeTransferApi { ThrowOnCancel = true };
        using var device = new TrackingDeviceHandle();
        var task = Start(device, new byte[8], api, cancellation.Token);
        cancellation.Cancel();
        bool completedEarly = task.IsCompleted;
        int freedEarly = api.FreeCount;
        api.Complete(TransferStatus.Completed, 0);

        await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.False(completedEarly);
        Assert.Equal(0, freedEarly);
        Assert.Equal(1, api.FreeCount);
    }

    [Fact]
    public async Task ContextDisposeTimeoutKeepsResourcesAndAllowsRetry()
    {
        using var native = new HotplugTests.FakeContextApi { DeviceHandleFactory = () => new TrackingDeviceHandle() };
        using var context = new UsbContext(native);
        using var device = native.RetainDevice((IntPtr)1, context);
        using var transferApi = new FakeTransferApi();
        device.Open();
        var handle = (TrackingDeviceHandle)device.DeviceHandle;
        var transfer = Start(handle, new byte[8], transferApi);
        var close = Task.Run(() => Assert.Throws<TimeoutException>(context.Dispose));
        await close.WaitAsync(TimeSpan.FromSeconds(8));
        bool disposedEarly = context.IsDisposed || context.IsDisposing;
        bool stoppedEarly = Volatile.Read(ref context.stopHandlingEvents) != 0;
        int freedEarly = transferApi.FreeCount;
        transferApi.Complete(TransferStatus.Cancelled, 0);
        await transfer;
        context.Dispose();

        Assert.False(disposedEarly);
        Assert.False(stoppedEarly);
        Assert.Equal(0, freedEarly);
        Assert.True(context.IsDisposed);
        Assert.Equal(1, handle.ReleaseCount);
    }

    [Fact]
    public async Task ConcurrentCancellationAndCompletionReleaseExactlyOnce()
    {
        int pendingBefore = AsyncTransfer.PendingCount;
        for (int iteration = 0; iteration < 250; iteration++)
        {
            using var api = new FakeTransferApi();
            using var device = new TrackingDeviceHandle();
            using var cancellation = new CancellationTokenSource();
            var task = Start(device, new byte[8], api, cancellation.Token);
            var cancel = Task.Run(cancellation.Cancel);
            api.Complete(TransferStatus.Completed, 8);
            await cancel;
            Assert.Equal(8, (await task).transferLength);
            await AsyncTransfer.CancelAndDrainAsync(device);
            Assert.Equal(1, api.FreeCount);
        }
        Assert.Equal(pendingBefore, AsyncTransfer.PendingCount);
    }

    [Fact]
    public async Task PublicAsyncOverloadsAcceptPreCancelledTokensWithoutNativeIo()
    {
        using var native = new HotplugTests.FakeContextApi { DeviceHandleFactory = () => new TrackingDeviceHandle() };
        using var context = new UsbContext(native);
        using var device = native.RetainDevice((IntPtr)1, context);
        device.Open();
        var reader = new UsbEndpointReader(device, 64, 0, ReadEndpointID.Ep01, EndpointType.Bulk);
        var writer = new UsbEndpointWriter(device, 0, WriteEndpointID.Ep01, EndpointType.Bulk);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(new byte[8], 0, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteAsync(new byte[8], 0, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.ControlTransferAsync(default, cancellation.Token));
    }

    [Fact]
    public async Task ClosingOneDeviceKeepsAnotherDevicesTransfersAndEventLoopAlive()
    {
        using var native = new HotplugTests.FakeContextApi { DeviceHandleFactory = () => new TrackingDeviceHandle() };
        using var context = new UsbContext(native);
        using var first = native.RetainDevice((IntPtr)1, context);
        using var second = native.RetainDevice((IntPtr)2, context);
        using var transferApi = new FakeTransferApi();
        first.Open();
        second.Open();
        var transfer = Start(second.DeviceHandle, new byte[8], transferApi);
        first.Close();
        bool stoppedEarly = Volatile.Read(ref context.stopHandlingEvents) != 0;
        int cancelledOther = transferApi.CancelCount;
        transferApi.Complete(TransferStatus.Completed, 8);
        Assert.Equal(8, (await transfer).transferLength);
        Assert.False(stoppedEarly);
        Assert.Equal(0, cancelledOther);
        Assert.True(second.IsOpen);
        second.Close();
        Assert.Empty(context.ReadOnlyOpenDevices);
    }

    [Fact]
    public void CloseClearsClaimAndAltSettingCachesForReopen()
    {
        using var native = new HotplugTests.FakeContextApi { DeviceHandleFactory = () => new TrackingDeviceHandle() };
        using var context = new UsbContext(native);
        using var device = native.RetainDevice((IntPtr)1, context);
        device.Open();
        // 只注入已缓存的 claim/alt 状态，避免把假句柄传给真实 libusb_claim_interface。
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var claimed = (List<int>)typeof(UsbDevice).GetField("mClaimedInterfaces", flags).GetValue(device);
        var alternate = (byte[])typeof(UsbDevice).GetField("usbAltInterfaceSettings", flags).GetValue(device);
        claimed.AddRange(new[] { 0, 1, 2 });
        alternate[1] = 2;

        device.Close();

        Assert.Empty(claimed);
        Assert.All(alternate, value => Assert.Equal((byte)0, value));
        device.Open();
        Assert.Throws<UsbException>(() => device.SetAltInterface(1));
        device.Close();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeviceReferencesKeepNativeContextAliveUntilTheirFinalRelease(bool openFirst)
    {
        using var native = new HotplugTests.FakeContextApi { DeviceHandleFactory = () => new TrackingDeviceHandle() };
        using var context = new UsbContext(native);
        using var first = native.RetainDevice((IntPtr)1, context);
        using var second = native.RetainDevice((IntPtr)2, context);
        var handle = (Context)typeof(UsbContext).GetField("context", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(context);
        if (openFirst) first.Open();

        context.Dispose();

        Assert.True(context.IsDisposed);
        Assert.False(handle.IsClosed);
        Assert.Throws<ObjectDisposedException>(second.Open);
        first.Dispose();
        Assert.False(handle.IsClosed);
        second.Dispose();
        Assert.True(handle.IsClosed);
        Assert.Equal(native.RetainedCount, native.ReleasedCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CloneAfterDeviceOrContextDisposeIsRejectedBeforeNativeIo(bool disposeContext)
    {
        using var native = new HotplugTests.FakeContextApi();
        using var context = new UsbContext(native);
        using var device = native.RetainDevice((IntPtr)1, context);
        if (disposeContext) context.Dispose();
        else device.Dispose();

        Assert.Throws<ObjectDisposedException>(() => device.Clone());
    }

    private sealed class TrackingDeviceHandle : DeviceHandle
    {
        public int ReleaseCount { get; private set; }

        public TrackingDeviceHandle() => SetHandle(new IntPtr(123));

        protected override bool ReleaseHandle()
        {
            ReleaseCount++;
            return true;
        }
    }

    private sealed unsafe class TrackingMemory : MemoryManager<byte>
    {
        private readonly byte[] storage = new byte[64];
        public int PinCount { get; private set; }
        public bool ThrowOnPin { get; set; }
        public Action OnUnpin { get; set; }

        public override Span<byte> GetSpan() => storage;

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            if (ThrowOnPin)
                throw new InvalidOperationException("Injected pin failure.");

            var handle = GCHandle.Alloc(storage, GCHandleType.Pinned);
            PinCount++;
            return new MemoryHandle((byte*)handle.AddrOfPinnedObject() + elementIndex, handle, this);
        }

        public override void Unpin()
        {
            OnUnpin?.Invoke();
            PinCount--;
        }

        protected override void Dispose(bool disposing)
        {
        }
    }

    private sealed unsafe class FakeTransferApi : IAsyncTransferApi, IDisposable
    {
        private Transfer* transfer;
        private readonly HashSet<IntPtr> allocations = new();
        public Error SubmitError { get; set; }
        public bool ThrowOnSubmit { get; set; }
        public bool ReturnNull { get; set; }
        public int AllocateCount { get; private set; }
        public int FreeCount { get; private set; }
        public int CancelCount { get; private set; }
        public Error CancelError { get; set; }
        public Action OnSubmit { get; set; }
        public bool ThrowOnCancel { get; set; }

        public Transfer* Allocate(int isoPackets)
        {
            AllocateCount++;
            if (ReturnNull)
                return null;

            transfer = (Transfer*)Marshal.AllocHGlobal(sizeof(Transfer));
            allocations.Add((IntPtr)transfer);
            *transfer = default;
            return transfer;
        }

        public Error Submit(Transfer* submitted)
        {
            Assert.Contains((IntPtr)submitted, allocations);
            transfer = submitted;
            if (ThrowOnSubmit)
                throw new InvalidOperationException("Injected submit failure.");

            OnSubmit?.Invoke();
            return SubmitError;
        }

        public Error Cancel(Transfer* submitted)
        {
            Assert.Contains((IntPtr)submitted, allocations);
            Assert.NotEqual(IntPtr.Zero, (IntPtr)submitted);
            CancelCount++;
            if (ThrowOnCancel) throw new InvalidOperationException("Injected cancellation failure.");
            return CancelError;
        }

        public void Free(Transfer* completed)
        {
            Assert.True(allocations.Remove((IntPtr)completed));
            Marshal.FreeHGlobal((IntPtr)completed);
            if (transfer == completed) transfer = null;
            FreeCount++;
        }

        public void Complete(TransferStatus status, int actualLength)
        {
            transfer->Status = status;
            transfer->ActualLength = actualLength;
            Marshal.GetDelegateForFunctionPointer<TransferDelegate>(transfer->Callback)(transfer);
        }

        public void Dispose()
        {
            Assert.Empty(allocations);
        }
    }
}
