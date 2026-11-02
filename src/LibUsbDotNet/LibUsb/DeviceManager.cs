using LibUsbDotNet.Main;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LibUsbDotNet.LibUsb;

/// <summary>独立等待设备到达，支持并发等待、取消与稳定连接检测。</summary>
public class DeviceManager : IDisposable
{
    private readonly bool _disposeContext;
    private readonly UsbContext _context;
    private readonly object _sync = new();
    private CancellationTokenSource _stopped;
    private bool _disposed;

    /// <summary>创建设备等待器；默认不取得 context 的所有权。</summary>
    /// <param name="context">需要监控的 USB context。</param>
    /// <param name="disposeContext">释放等待器时是否同时释放 context。</param>
    public DeviceManager(UsbContext context, bool disposeContext = false)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _disposeContext = disposeContext;
    }

    /// <summary>开始监控；重复调用不会重复订阅或取消已有等待。</summary>
    public void Start()
    {
        lock (_sync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DeviceManager));
            if (_stopped != null) return;
            _context.RegisterHotPlug();
            _stopped = new CancellationTokenSource();
        }
    }

#nullable enable
    /// <summary>等待匹配设备，已有设备也会检查稳定连接时间。</summary>
    /// <param name="finder">设备匹配条件。</param>
    /// <param name="maxWaitTime">总等待上限，支持 Timeout.InfiniteTimeSpan。</param>
    /// <param name="cancellationToken">取消当前等待，不影响其他等待。</param>
    /// <param name="stableConnectionInterval">设备必须持续连接的最短时间，计入总等待上限。</param>
    /// <returns>由调用方负责 Dispose 的独立设备引用；超时返回 null。</returns>
    public Task<UsbDevice?> WaitForDeviceArrival(UsbDeviceFinder finder, TimeSpan maxWaitTime,
        CancellationToken cancellationToken, TimeSpan stableConnectionInterval = default) =>
        WaitForArrival(finder, maxWaitTime, cancellationToken, stableConnectionInterval, false);

    /// <summary>只等待本次调用之后的新到达；适合设备重启后的重新枚举。</summary>
    /// <param name="finder">设备匹配条件。</param>
    /// <param name="maxWaitTime">总等待上限，支持 Timeout.InfiniteTimeSpan。</param>
    /// <param name="cancellationToken">取消当前等待，不影响其他等待。</param>
    /// <param name="stableConnectionInterval">设备必须持续连接的最短时间，计入总等待上限。</param>
    /// <returns>由调用方负责 Dispose 的独立设备引用；超时返回 null。</returns>
    public Task<UsbDevice?> WaitForNewDeviceArrival(UsbDeviceFinder finder, TimeSpan maxWaitTime,
        CancellationToken cancellationToken, TimeSpan stableConnectionInterval = default) =>
        WaitForArrival(finder, maxWaitTime, cancellationToken, stableConnectionInterval, true);

    private async Task<UsbDevice?> WaitForArrival(UsbDeviceFinder finder, TimeSpan maxWaitTime,
        CancellationToken cancellationToken, TimeSpan stableInterval, bool onlyNew)
    {
        if (finder == null) throw new ArgumentNullException(nameof(finder));
        if (maxWaitTime < TimeSpan.Zero && maxWaitTime != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(maxWaitTime));
        if (stableInterval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(stableInterval));

        CancellationTokenSource linked;
        lock (_sync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DeviceManager));
            if (_stopped == null) throw new InvalidOperationException("Start must be called before waiting for devices.");
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopped.Token);
        }
        using (linked)
        {
            var timer = Stopwatch.StartNew();
            var snapshot = _context.GetHotplugSnapshot();
            long initialGeneration = snapshot.Generation;
            long initialSequence = onlyNew ? snapshot.Sequence : -1;
            UsbContext.HotplugDevice? candidate = null;
            TimeSpan candidateSince = default;
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (!snapshot.Active || snapshot.Generation != initialGeneration)
                    throw new OperationCanceledException("USB hotplug monitoring stopped.");
                if (candidate != null && !snapshot.Devices.Contains(candidate)) candidate = null;
                if (candidate == null)
                {
                    foreach (var entry in snapshot.Devices)
                    {
                        if (entry.Arrival <= initialSequence) continue;
                        using var matchDevice = _context.CloneHotplugDevice(entry);
                        if (matchDevice != null && finder.Check(matchDevice))
                        {
                            candidate = entry;
                            break;
                        }
                    }
                    candidateSince = timer.Elapsed;
                }

                TimeSpan remaining = maxWaitTime == Timeout.InfiniteTimeSpan ? TimeSpan.MaxValue : maxWaitTime - timer.Elapsed;
                TimeSpan stableRemaining = candidate == null ? TimeSpan.MaxValue : stableInterval - (timer.Elapsed - candidateSince);
                if (candidate != null && stableRemaining <= TimeSpan.Zero && (remaining >= TimeSpan.Zero || maxWaitTime == TimeSpan.Zero))
                {
                    var result = _context.CloneHotplugDevice(candidate);
                    if (result != null) return result;
                }
                if (remaining <= TimeSpan.Zero) return null;

                var delay = remaining < stableRemaining ? remaining : stableRemaining;
                if (delay > TimeSpan.FromMilliseconds(int.MaxValue)) delay = TimeSpan.FromMilliseconds(int.MaxValue);
                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                using (var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(linked.Token))
                {
                    var timeout = Task.Delay(delay, delayCancellation.Token);
                    await Task.WhenAny(snapshot.Changed, timeout).ConfigureAwait(false);
                    delayCancellation.Cancel(); // 变化先到时移除尚未触发的超时计时器。
                }
                snapshot = _context.GetHotplugSnapshot();
            }
        }
    }
#nullable disable

    /// <summary>取消本等待器的全部等待；保留 context 的共享热插拔监控。</summary>
    public void Stop()
    {
        CancellationTokenSource stopped;
        lock (_sync)
        {
            stopped = _stopped;
            _stopped = null;
        }
        if (stopped == null) return;
        stopped.Cancel();
        stopped.Dispose();
    }

    /// <summary>停止等待；仅在显式取得所有权时释放 context。</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
        if (_disposeContext) _context.Dispose();
    }
}
