// Copyright © 2006-2010 Travis Robinson. All rights reserved.
// Copyright © 2011-2023 LibUsbDotNet contributors. All rights reserved.
// 
// website: http://github.com/libusbdotnet/libusbdotnet
// 
// This program is free software; you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation; either version 2 of the License, or 
// (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful, but 
// WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY
// or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License
// for more details.
// 
// You should have received a copy of the GNU General Public License along
// with this program; if not, write to the Free Software Foundation, Inc.,
// 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA. or 
// visit www.gnu.org.
// 
//

using LibUsbDotNet.Main;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace LibUsbDotNet.LibUsb;

/// <summary>
/// Handles submission and awaiting of asynchronous transfers.
/// </summary>
internal static class AsyncTransfer
{
    /// <summary>按打开句柄隔离提交与关闭；弱键不延长已关闭句柄的生命周期。</summary>
    private sealed class DeviceTransfers
    {
        internal readonly object Sync = new();
        internal readonly HashSet<TransferCallbackCompletion> Active = new();
        internal readonly List<TransferCallbackCompletion> Idle = new();
        internal WeakReference<UsbContext> Context;
        internal bool Closing;
        internal TaskCompletionSource<bool> Drained;
    }

    /// <summary>池随句柄关闭释放；异常遗失句柄时仍由SafeHandle回收原生分配。</summary>
    private sealed unsafe class NativeTransferHandle : SafeHandle
    {
        private readonly IAsyncTransferApi api;
        internal NativeTransferHandle(IAsyncTransferApi api, int isoPackets) : base(IntPtr.Zero, true)
        {
            this.api = api;
            SetHandle((IntPtr)api.Allocate(isoPackets));
            if (IsInvalid) throw new OutOfMemoryException("libusb could not allocate an asynchronous transfer.");
        }
        public override bool IsInvalid => handle == IntPtr.Zero;
        protected override bool ReleaseHandle() { api.Free((Transfer*)handle); return true; }
    }

    private sealed class TransferCallbackCompletion : IDisposable
    {
        private MemoryHandle memoryHandle;
        private readonly DeviceHandle device;
        private UsbContext context;
        private int disposed;
        internal readonly DeviceTransfers Owner;
        internal readonly IAsyncTransferApi NativeApi;
        internal readonly NativeTransferHandle Native;
        internal readonly int IsoPackets;
        internal CancellationToken Token;
        internal TaskCompletionSource<(Error error, int transferLength)> Completion;
        internal CancellationTokenRegistration Cancellation;
        internal IntPtr Pointer => Native.DangerousGetHandle();
        internal bool Submitted;
        internal Exception CancelFailure;
        private bool cancelRequested;

        internal TransferCallbackCompletion(DeviceTransfers owner, IAsyncTransferApi nativeApi, DeviceHandle device, int isoPackets)
        {
            Owner = owner;
            NativeApi = nativeApi;
            this.device = device;
            IsoPackets = isoPackets;
            Native = new(nativeApi, isoPackets);
        }

        internal void Initialize(MemoryHandle memoryHandle, CancellationToken token)
        {
            this.memoryHandle = memoryHandle;
            disposed = 0;
            Token = token;
            Submitted = false;
            cancelRequested = false;
            CancelFailure = null;
            Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Owner.Context?.TryGetTarget(out context);
        }

        /// <summary>取消仅发请求，不释放资源；最终回调与取消共用句柄锁，防止释放后取消。</summary>
        internal unsafe void Cancel()
        {
            lock (Owner.Sync)
            {
                if (cancelRequested || !Submitted) return;
                cancelRequested = true;
                // NotFound 表示完成已在途中；其它错误同样不能作为已完成的证明。
                try { NativeApi.Cancel((Transfer*)Pointer); }
                catch (Exception exception) { CancelFailure = exception; }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { memoryHandle.Dispose(); }
            finally
            {
                device.DangerousRelease();
                GC.KeepAlive(context);
                context = null;
                memoryHandle = default;
            }
        }

        /// <summary>取消注册及pin均已排空才允许复用；旧Task独立保留结果，不能重置给下一笔。</summary>
        internal unsafe void Recycle(bool success)
        {
            if (!success || Owner.Closing || device.IsClosed || Owner.Idle.Count == 16)
            {
                Native.Dispose();
                return;
            }
            var transfer = (Transfer*)Pointer;
            transfer->Buffer = null;
            transfer->DevHandle = IntPtr.Zero;
            transfer->UserData = IntPtr.Zero;
            Completion = null;
            Token = default;
            Cancellation = default;
            Owner.Idle.Add(this);
        }
    }

    private static readonly ConditionalWeakTable<DeviceHandle, DeviceTransfers> Devices = new();
    private static int transferIndex;
    private static readonly unsafe TransferDelegate TransferCallback = new(Callback);
    private static readonly IntPtr TransferDelegatePtr = Marshal.GetFunctionPointerForDelegate(TransferCallback);
    private static readonly ConcurrentDictionary<int, TransferCallbackCompletion> TransferDictionary = new();

    /// <summary>Number of transfers still owning a completion registration.</summary>
    internal static int PendingCount => TransferDictionary.Count;

    /// <summary>仅在途传输强持有 context，避免事件线程故障后 context 被提前终结。</summary>
    internal static void BindContext(DeviceHandle device, UsbContext context)
    {
        var owner = Devices.GetValue(device, _ => new DeviceTransfers());
        lock (owner.Sync) owner.Context = new WeakReference<UsbContext>(context);
    }

    public static unsafe Task<(Error error, int transferLength)> TransferAsync(
        DeviceHandle device,
        byte endPoint,
        EndpointType endPointType,
        Memory<byte> buffer,
        int timeout,
        int isoPacketSize = 0,
        IAsyncTransferApi nativeApi = null,
        CancellationToken cancellationToken = default)
    {
        if (device == null) throw new ArgumentNullException(nameof(device));
        cancellationToken.ThrowIfCancellationRequested();
        nativeApi ??= AsyncTransferApi.Instance;
        var owner = Devices.GetValue(device, _ => new DeviceTransfers());
        MemoryHandle memoryHandle = default;
        bool deviceReference = false;
        bool registered = false;
        int transferId = 0;
        Transfer* transfer = null;
        TransferCallbackCompletion completion = null;

        try
        {
            lock (owner.Sync)
            {
                try
                {
                    if (owner.Closing) throw new ObjectDisposedException(nameof(DeviceHandle), "The device is closing.");
                    device.DangerousAddRef(ref deviceReference);
                    if (device.IsInvalid) throw new ArgumentException("The device handle is invalid.", nameof(device));

                    memoryHandle = buffer.Pin();
                    int numIsoPackets = isoPacketSize > 0 ? buffer.Length / isoPacketSize : 0;
                    for (int i = owner.Idle.Count - 1; i >= 0; i--)
                    {
                        var idle = owner.Idle[i];
                        if (!ReferenceEquals(idle.NativeApi, nativeApi) || idle.IsoPackets != numIsoPackets) continue;
                        completion = idle;
                        owner.Idle.RemoveAt(i);
                        break;
                    }
                    completion ??= new TransferCallbackCompletion(owner, nativeApi, device, numIsoPackets);
                    completion.Initialize(memoryHandle, cancellationToken);
                    transfer = (Transfer*)completion.Pointer;

                    do { transferId = Interlocked.Increment(ref transferIndex); }
                    while (!TransferDictionary.TryAdd(transferId, completion));
                    registered = true;
                    owner.Active.Add(completion);

                    transfer->DevHandle = device.DangerousGetHandle();
                    transfer->Endpoint = endPoint;
                    transfer->Timeout = (uint)timeout;
                    transfer->Type = (byte)endPointType;
                    transfer->Buffer = (byte*)memoryHandle.Pointer;
                    transfer->Length = buffer.Length;
                    transfer->NumIsoPackets = numIsoPackets;
                    transfer->Flags = (byte)TransferFlags.None;
                    transfer->Callback = TransferDelegatePtr;
                    transfer->UserData = new IntPtr(transferId);

                    // 提交前注册以覆盖取消竞态；提交成功后再检查取消状态。
                    completion.Cancellation = cancellationToken.Register(state => ((TransferCallbackCompletion)state).Cancel(), completion);
                    TransferEvents.Log.SubmitAttempt(transferId, endPoint, buffer.Length);
                    nativeApi.Submit(transfer).ThrowOnError();
                    completion.Submitted = true;
                    if (cancellationToken.IsCancellationRequested) completion.Cancel();
                    return completion.Completion.Task;
                }
                catch
                {
                    if (registered) TransferDictionary.TryRemove(transferId, out _);
                    if (completion != null)
                    {
                        completion.Submitted = false;
                        owner.Active.Remove(completion);
                        completion.Native.Dispose();
                    }
                    throw;
                }
            }
        }
        catch
        {
            // 不持有 Owner.Sync 等待取消回调退出，避免 Cancellation.Dispose 与 Cancel 互锁。
            if (completion != null)
            {
                completion.Cancellation.Dispose();
                completion.Dispose();
            }
            else
            {
                memoryHandle.Dispose();
                if (deviceReference) device.DangerousRelease();
            }
            throw;
        }
    }

    private static unsafe void Callback(Transfer* transfer)
    {
        int transferId = transfer->UserData.ToInt32();
        TransferEvents.Log.NativeCompleted(transferId, transfer->Endpoint, (int)transfer->Status, transfer->ActualLength);
        if (!TransferDictionary.TryRemove(transferId, out var completion)) return;

        var result = (GetErrorFromTransferStatus(transfer->Status), transfer->ActualLength);
        bool cancelled = transfer->Status == TransferStatus.Cancelled && completion.Token.IsCancellationRequested;
        Exception failure = null;
        try
        {
            try
            {
                lock (completion.Owner.Sync)
                {
                    failure = completion.CancelFailure;
                    completion.Submitted = false;
                }
            }
            finally
            {
                try { completion.Cancellation.Dispose(); }
                finally { completion.Dispose(); }
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            lock (completion.Owner.Sync)
            {
                var task = completion.Completion;
                var token = completion.Token;
                try { completion.Recycle(failure == null && transfer->Status == TransferStatus.Completed); }
                catch (Exception exception) { failure = exception; }
                completion.Owner.Active.Remove(completion);
                if (failure != null) task.TrySetException(failure);
                else if (cancelled) task.TrySetCanceled(token);
                else task.TrySetResult(result);
                if (completion.Owner.Active.Count == 0) completion.Owner.Drained?.TrySetResult(true);
            }
        }
    }

    /// <summary>禁止新提交并请求取消；返回值只在最终回调释放全部资源后完成。</summary>
    internal static Task CancelAndDrainAsync(DeviceHandle device)
    {
        var owner = Devices.GetValue(device, _ => new DeviceTransfers());
        lock (owner.Sync)
        {
            owner.Closing = true;
            foreach (var idle in owner.Idle) idle.Native.Dispose();
            owner.Idle.Clear();
            var pending = owner.Active.ToArray();
            if (pending.Length == 0) return Task.CompletedTask;
            owner.Drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            foreach (var completion in pending) completion.Cancel();
            return owner.Drained.Task;
        }
    }

    private static Error GetErrorFromTransferStatus(TransferStatus status)
    {
        Error ret;

        switch (status)
        {
            case TransferStatus.Completed:
                ret = Error.Success;
                break;

            case TransferStatus.TimedOut:
                ret = Error.Timeout;
                break;

            case TransferStatus.Stall:
                ret = Error.Pipe;
                break;

            case TransferStatus.Overflow:
                ret = Error.Overflow;
                break;

            case TransferStatus.NoDevice:
                ret = Error.NoDevice;
                break;

            case TransferStatus.Error:
            case TransferStatus.Cancelled:
                ret = Error.Io;
                break;

            default:
                ret = Error.Other;
                break;
        }

        return ret;
    }
}
