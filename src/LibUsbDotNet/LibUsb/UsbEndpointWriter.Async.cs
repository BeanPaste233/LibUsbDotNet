using System;
using System.Threading;
using System.Threading.Tasks;

namespace LibUsbDotNet.LibUsb;

public partial class UsbEndpointWriter
{
    /// <summary>异步发送并支持取消；最终回调前不会归还内部发送缓冲区。</summary>
    /// <param name="buffer">待发送数据。</param>
    /// <param name="timeout">原生传输超时，单位毫秒。</param>
    /// <param name="cancellationToken">取消请求。</param>
    /// <returns>原生状态和实际长度；请求取消且原生返回取消时抛 OperationCanceledException。</returns>
    public virtual Task<(Error error, int transferLength)> WriteAsync(ReadOnlyMemory<byte> buffer, int timeout, CancellationToken cancellationToken) =>
        TransferAsync(buffer, timeout, cancellationToken);

    /// <summary>对指定发送区间执行可取消异步发送。</summary>
    public virtual Task<(Error error, int transferLength)> WriteAsync(ReadOnlyMemory<byte> buffer, int offset, int length, int timeout, CancellationToken cancellationToken) =>
        TransferAsync(buffer.Slice(offset, length), timeout, cancellationToken);

    /// <summary>
    /// Writes data asynchronously to the current <see cref="UsbEndpointWriter"/>.
    /// </summary>
    /// <param name="buffer">The buffer storing the data to write.</param>
    /// <param name="timeout">Maximum time to wait for the transfer to complete.  If the transfer times out, the IO operation will be cancelled.</param>
    /// <returns>
    /// Tuple of (<see cref="Error"/> error, <see cref="int"/> transferLength). error is <see cref="Error.Success"/> on success.
    /// </returns>
    public virtual async Task<(Error error, int transferLength)> WriteAsync(ReadOnlyMemory<byte> buffer, int timeout) =>
        await TransferAsync(buffer, timeout).ConfigureAwait(false);

    /// <summary>
    /// Writes data asynchronously to the current <see cref="UsbEndpointWriter"/>.
    /// </summary>
    /// <param name="buffer">The buffer storing the data to write.</param>
    /// <param name="offset">The position in buffer to start writing the data from.</param>
    /// <param name="length">The number of bytes to write.</param>
    /// <param name="timeout">Maximum time to wait for the transfer to complete.  If the transfer times out, the IO operation will be cancelled.</param>
    /// <returns>
    /// Tuple of (<see cref="Error"/> error, <see cref="int"/> transferLength). error is <see cref="Error.Success"/> on success.
    /// </returns>
    public virtual async Task<(Error error, int transferLength)> WriteAsync(ReadOnlyMemory<byte> buffer, int offset, int length, int timeout) =>
        await TransferAsync(buffer.Slice(offset, length), timeout).ConfigureAwait(false);
}
