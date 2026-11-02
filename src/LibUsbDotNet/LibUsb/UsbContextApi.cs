// SPDX-FileCopyrightText: 2026 BeanPaste233
// SPDX-License-Identifier: LGPL-3.0-or-later

using System;
using LibUsbDotNet.Info;

namespace LibUsbDotNet.LibUsb;

/// <summary>原生事件入口，供生命周期回归测试注入确定性故障。</summary>
internal interface IUsbContextApi
{
    bool SupportsHotplug { get; }
    Error Register(Context context, HotplugOptions options, IntPtr callback, ref int handle);
    void Deregister(Context context, int handle);
    Error HandleEvents(Context context, ref int completed);
    void Interrupt(Context context);
    UsbDevice RetainDevice(IntPtr device, UsbContext context);
    CachedDeviceInfo GetDeviceInfo(UsbDevice device);
    UsbDeviceCollection List(UsbContext context);
    Error Open(Device device, out DeviceHandle handle);
}

internal sealed class UsbContextApi : IUsbContextApi
{
    internal static readonly UsbContextApi Instance = new();

    public bool SupportsHotplug => NativeMethods.HasCapability((uint)Capability.HasHotplug) != 0;

    public Error Register(Context context, HotplugOptions options, IntPtr callback, ref int handle) =>
        NativeMethods.HotplugRegisterCallback(context,
            HotplugEvent.DeviceArrived | HotplugEvent.DeviceLeft, HotplugFlag.Enumerate,
            options.VendorId, options.ProductId, options.DeviceClass, callback, IntPtr.Zero, ref handle);

    public void Deregister(Context context, int handle) => NativeMethods.HotplugDeregisterCallback(context, handle);
    public Error HandleEvents(Context context, ref int completed) => NativeMethods.HandleEventsCompleted(context, ref completed);
    public void Interrupt(Context context) => NativeMethods.InterruptEventHandler(context);

    public UsbDevice RetainDevice(IntPtr device, UsbContext context)
    {
        using var borrowed = Device.DangerousCreate(device, false, true);
        return new UsbDevice(NativeMethods.RefDevice(borrowed), context);
    }

    public CachedDeviceInfo GetDeviceInfo(UsbDevice device) => new(device);
    public UsbDeviceCollection List(UsbContext context) => context.List();

    public Error Open(Device device, out DeviceHandle handle)
    {
        IntPtr pointer = IntPtr.Zero;
        var error = NativeMethods.Open(device, ref pointer);
        handle = error == Error.Success ? DeviceHandle.DangerousCreate(pointer) : null;
        return error;
    }
}
