using System.Diagnostics.Tracing;

namespace LibUsbDotNet.LibUsb;

/// <summary>可选 EventPipe 事件；默认关闭，不复制载荷、不在原生回调中落盘。</summary>
[EventSource(Name = "DM-LibUsb-Transfers")]
internal sealed class TransferEvents : EventSource
{
    internal static readonly TransferEvents Log = new();

    /// <summary>原生 Submit 调用前的主机事件，不表示设备已收到数据。</summary>
    [Event(1, Level = EventLevel.Verbose)]
    public void SubmitAttempt(int id, int endpoint, int bytes)
    {
        if (IsEnabled()) WriteEvent(1, id, endpoint, bytes);
    }

    /// <summary>进入最终原生回调；与提交事件按进程内 id 对应。</summary>
    [Event(2, Level = EventLevel.Verbose)]
    public void NativeCompleted(int id, int endpoint, int status, int bytes)
    {
        if (IsEnabled()) WriteEvent(2, id, endpoint, status, bytes);
    }
}
