// SPDX-FileCopyrightText: 2026 BeanPaste233
// SPDX-License-Identifier: LGPL-3.0-or-later

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace LibUsbDotNet.LibUsb;

/// <summary>顺序派发通知，不占用 libusb 事件线程，也不让订阅者异常越过回调边界。</summary>
internal sealed class DeviceEventQueue
{
    internal const int Capacity = 256;
    private readonly object sync = new();
    private Task tail = Task.CompletedTask;
    private int pending;
    private bool overflow;
    [ThreadStatic] private static DeviceEventQueue executingQueue;
    internal Action<Exception> ErrorHandler { get; set; }

    internal bool Post(Action action)
    {
        lock (sync)
        {
            if (pending >= Capacity)
            {
                overflow = true;
                return false;
            }
            pending++;
            tail = tail.ContinueWith(_ =>
            {
                executingQueue = this;
                try { action(); }
                catch (Exception ex) { Trace.TraceError("USB notification failed: {0}", ex); }
                finally
                {
                    bool reportOverflow;
                    lock (sync)
                    {
                        pending--;
                        reportOverflow = overflow;
                        overflow = false;
                    }
                    try
                    {
                        if (reportOverflow)
                            ErrorHandler?.Invoke(new InvalidOperationException("USB notification queue overflow; refresh the device list. Cached state remains current."));
                    }
                    catch (Exception ex) { Trace.TraceError("USB error handler failed: {0}", ex); }
                    finally { executingQueue = null; }
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return true;
        }
    }

    internal void Drain()
    {
        // 通知内允许注销/释放 context，不能等待正在执行自己的任务。
        if (executingQueue == this)
            return;

        Task pending;
        lock (sync) pending = tail;
        pending.GetAwaiter().GetResult();
    }
}
