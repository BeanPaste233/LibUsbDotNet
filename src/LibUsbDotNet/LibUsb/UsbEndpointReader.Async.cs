using System;
using System.Threading;
using System.Threading.Tasks;

namespace LibUsbDotNet.LibUsb;

public partial class UsbEndpointReader
{
    /// <summary>异步读取并支持取消；最终原生回调到达前保持缓冲区固定。</summary>
    /// <param name="buffer">接收缓冲区，Task 完成前不得复用。</param>
    /// <param name="timeout">原生传输超时，单位毫秒。</param>
    /// <param name="cancellationToken">取消请求；实际完成前不会提前返回。</param>
    /// <returns>原生状态和实际长度；请求取消且原生返回取消时抛 OperationCanceledException。</returns>
    public virtual Task<(Error error, int transferLength)> ReadAsync(Memory<byte> buffer, int timeout, CancellationToken cancellationToken) =>
        TransferAsync(buffer, timeout, cancellationToken);

    /// <summary>对指定接收区间执行可取消异步读取。</summary>
    public virtual Task<(Error error, int transferLength)> ReadAsync(Memory<byte> buffer, int offset, int length, int timeout, CancellationToken cancellationToken) =>
        TransferAsync(buffer.Slice(offset, length), timeout, cancellationToken);

    /// <summary>
    /// Reads data asynchronously from the current <see cref="UsbEndpointReader"/>.
    /// </summary>
    /// <param name="buffer">The buffer to store the received data in.</param>
    /// <param name="timeout">Maximum time to wait for the transfer to complete.  If the transfer times out, the IO operation will be cancelled.</param>
    /// <returns>
    /// Tuple of (<see cref="Error"/> error, <see cref="int"/> transferLength). error is <see cref="Error.Success"/> on success.
    /// </returns>
    public virtual async Task<(Error error, int transferLength)> ReadAsync(Memory<byte> buffer, int timeout) =>
        await TransferAsync(buffer, timeout).ConfigureAwait(false);

    /// <summary>
    /// Reads data asynchronously from the current <see cref="UsbEndpointReader"/>.
    /// </summary>
    /// <param name="buffer">The buffer to store the received data in.</param>
    /// <param name="offset">The position in buffer to start storing the data.</param>
    /// <param name="length">The maximum number of bytes to receive.</param>
    /// <param name="timeout">Maximum time to wait for the transfer to complete.  If the transfer times out, the IO operation will be cancelled.</param>
    /// <returns>
    /// Tuple of (<see cref="Error"/> error, <see cref="int"/> transferLength). error is <see cref="Error.Success"/> on success.
    /// </returns>
    public virtual async Task<(Error error, int transferLength)> ReadAsync(Memory<byte> buffer, int offset, int length, int timeout) =>
        await TransferAsync(buffer.Slice(offset, length), timeout).ConfigureAwait(false);

    /// <summary>
    /// Reads/discards data asynchronously from the endpoint until no more data is available.
    /// </summary>
    /// <returns>Always returns <see cref="Error.Success"/> </returns>
    public virtual async Task<Error> ReadFlushAsync()
    {
        byte[] bufDummy = new byte[64];
        int iBufCount = 0;

        Error error = Error.Success;

        while (error == Error.Success && iBufCount < 128)
        {
            (error, _) = await ReadAsync(bufDummy, 10).ConfigureAwait(false);
            iBufCount++;
        }

        return Error.Success;
    }
}
