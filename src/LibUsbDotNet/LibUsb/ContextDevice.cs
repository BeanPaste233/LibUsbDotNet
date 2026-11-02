// SPDX-FileCopyrightText: 2026 BeanPaste233
// SPDX-License-Identifier: LGPL-3.0-or-later

namespace LibUsbDotNet.LibUsb;

/// <summary>设备引用归还前保活原生 context，避免先 Dispose context 后 unref 访问失效内存。</summary>
internal sealed class ContextDevice : Device
{
    private readonly Device reference;
    private readonly Context context;

    /// <summary>接管设备引用；SafeHandle 的引用计数保证 context 不会提前调用 libusb_exit。</summary>
    internal ContextDevice(Device reference, Context context)
    {
        this.reference = reference;
        this.context = context;
        bool retained = false;
        context.DangerousAddRef(ref retained);
        SetHandle(reference.DangerousGetHandle());
    }

    /// <summary>先归还设备引用，最后才允许原生 context 释放；遗忘 Dispose 时也遵循该顺序。</summary>
    protected override bool ReleaseHandle()
    {
        try
        {
            reference.Dispose();
            return true;
        }
        finally
        {
            context.DangerousRelease();
        }
    }
}
