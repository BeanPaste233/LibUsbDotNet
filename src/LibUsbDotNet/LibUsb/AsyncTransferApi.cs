// SPDX-FileCopyrightText: 2026 BeanPaste233
// SPDX-License-Identifier: LGPL-3.0-or-later

namespace LibUsbDotNet.LibUsb;

/// <summary>
/// Native transfer operations separated for deterministic failure-path tests.
/// </summary>
internal unsafe interface IAsyncTransferApi
{
    /// <summary>Allocates a transfer with the requested ISO packet capacity.</summary>
    Transfer* Allocate(int isoPackets);

    /// <summary>Submits a transfer, retaining caller ownership on failure.</summary>
    Error Submit(Transfer* transfer);

    /// <summary>请求异步取消；必须等待最终回调后才能释放传输。</summary>
    Error Cancel(Transfer* transfer);

    /// <summary>Frees an unsubmitted or completed transfer.</summary>
    void Free(Transfer* transfer);
}

/// <summary>Production operations backed by the dynamically loaded libusb.</summary>
internal sealed unsafe class AsyncTransferApi : IAsyncTransferApi
{
    internal static readonly AsyncTransferApi Instance = new();

    private AsyncTransferApi()
    {
    }

    /// <inheritdoc/>
    public Transfer* Allocate(int isoPackets) => NativeMethods.AllocTransfer(isoPackets);

    /// <inheritdoc/>
    public Error Submit(Transfer* transfer) => NativeMethods.SubmitTransfer(transfer);

    /// <inheritdoc/>
    public Error Cancel(Transfer* transfer) => NativeMethods.CancelTransfer(transfer);

    /// <inheritdoc/>
    public void Free(Transfer* transfer) => NativeMethods.FreeTransfer(transfer);
}
