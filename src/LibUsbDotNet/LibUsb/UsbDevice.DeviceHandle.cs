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

using LibUsbDotNet.Descriptors;
using LibUsbDotNet.Main;
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace LibUsbDotNet.LibUsb;

// Implementation of functionality which wraps around a DeviceHandle.
public partial class UsbDevice
{
    /// <summary>
    /// Timeout for all control transfers in milliseconds.
    /// </summary>
    public static int ControlTransferTimeout { get; set; } = UsbConstants.DefaultTimeout;

    /// <summary>
    /// The underlying device handle. The handle is populated when you open the device
    /// using <see cref="Open"/>, and cleared when you close the device using <see cref="Close"/>.
    /// </summary>
    private DeviceHandle deviceHandle;

    /// <inheritdoc/>
    public DeviceHandle DeviceHandle
    {
        get { return this.deviceHandle; }
    }

    /// <summary>
    /// Gets a value indicating whether the device has been opened. You can perform I/O on a
    /// device when it is open.
    /// </summary>
    public bool IsOpen
    {
        get
        {
            return this.deviceHandle != null;
        }
    }

    /// <summary>
    /// Gets the <c>bConfigurationValue</c> of the currently active configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// You could formulate your own control request to obtain this information, but this function
    /// has the advantage that it may be able to retrieve the information from operating system caches
    /// (no I/O involved).
    /// </para>
    /// <para>
    /// If the OS does not cache this information, then this function will block while a control
    /// transfer is submitted to retrieve the information.
    /// </para>
    /// <para>
    /// This property will return a value of 0 in the config output parameter if the device is
    /// in unconfigured state.
    /// </para>
    /// </remarks>
    public int Configuration
    {
        get
        {
            this.EnsureNotDisposed();
            this.EnsureOpen();

            int config = 0;
            NativeMethods.GetConfiguration(this.deviceHandle, ref config).ThrowOnError();
            return config;
        }
    }

    /// <inheritdoc/>
    public void SetConfiguration(int config)
    {
        this.EnsureNotDisposed();
        this.EnsureOpen();

        NativeMethods.SetConfiguration(this.deviceHandle, config).ThrowOnError();
    }

    /// <summary>
    /// Retrieve a descriptor from a device.
    /// </summary>
    /// <param name="descriptorIndex">
    /// The index of the descriptor to retieve.
    /// </param>
    /// <param name="failSilently">
    /// <see langword="true"/> to return <see langword="null"/> when the descriptor could not be
    /// received; <see langword="false"/> to throw an <see cref="UsbException"/> instead.
    /// </param>
    /// <returns>
    /// The value of the requested descriptor.
    /// </returns>
    public unsafe string GetStringDescriptor(byte descriptorIndex, bool failSilently = false)
    {
        if (failSilently && !this.IsOpen)
        {
            return null;
        }

        this.EnsureNotDisposed();
        this.EnsureOpen();

        if (descriptorIndex == 0)
            return null;

        Span<byte> buffer = stackalloc byte[1024];
        int length;
        fixed (byte* ptr = &MemoryMarshal.GetReference(buffer))
            length = (int)NativeMethods.GetStringDescriptorAscii(this.deviceHandle, descriptorIndex, ptr,
                buffer.Length);

        if (length < 0)
        {
            if (failSilently)
                return null;

            ((Error)length).ThrowOnError();
        }

        return Encoding.ASCII.GetString(buffer[..length].ToArray());
    }

    /// <inheritdoc/>
    public unsafe int ControlTransfer(UsbSetupPacket setupPacket)
    {
        return this.ControlTransfer(setupPacket, null, 0, 0);
    }

    /// <inheritdoc/>
    public unsafe int ControlTransfer(UsbSetupPacket setupPacket, byte[] buffer, int offset, int length)
    {
        this.EnsureNotDisposed();
        this.EnsureOpen();

        int result = 0;

        if (length > 0)
        {
            fixed (byte* data = &buffer[0])
            {
                result = NativeMethods.ControlTransfer(
                    this.deviceHandle,
                    setupPacket.RequestType,
                    setupPacket.Request,
                    (ushort)setupPacket.Value,
                    (ushort)setupPacket.Index,
                    data,
                    (ushort)length,
                    (uint)ControlTransferTimeout);
            }
        }
        else
        {
            result = NativeMethods.ControlTransfer(
                this.deviceHandle,
                setupPacket.RequestType,
                setupPacket.Request,
                (ushort)setupPacket.Value,
                (ushort)setupPacket.Index,
                null,
                0,
                (uint)ControlTransferTimeout);
        }

        if (result >= 0)
        {
            return result;
        }
        else
        {
            throw new UsbException((Error)result);
        }
    }

    /// <inheritdoc/>
    public unsafe bool GetDescriptor(byte descriptorType, byte index, short langId, IntPtr buffer, int bufferLength, out int transferLength)
    {
        this.EnsureNotDisposed();
        this.EnsureOpen();

        int ret = NativeMethods.ControlTransfer(
            this.deviceHandle,
            (byte)EndpointDirection.In,
            (byte)StandardRequest.GetDescriptor,
            (ushort)((descriptorType << 8) | index),
            0,
            (byte*)buffer.ToPointer(),
            (ushort)bufferLength,
            1000);

        if (ret < 0)
        {
            throw new UsbException((Error)ret);
        }

        transferLength = ret;
        return true;
    }

    /// <inheritdoc/>
    public bool GetDescriptor(byte descriptorType, byte index, short langId, object buffer, int bufferLength, out int transferLength)
    {
        using (PinnedHandle p = new PinnedHandle(buffer))
        {
            return this.GetDescriptor(descriptorType, index, langId, p.Handle, bufferLength, out transferLength);
        }
    }

    /// <inheritdoc/>
    public bool GetLangIDs(out short[] langIDs)
    {
        this.EnsureNotDisposed();
        this.EnsureOpen();

        LangStringDescriptor sd = new LangStringDescriptor(UsbDescriptor.Size + (16 * sizeof(short)));

        int ret;
        bool bSuccess = this.GetDescriptor((byte)DescriptorType.String, 0, 0, sd.Ptr, sd.MaxSize, out ret);
        bSuccess = sd.Get(out langIDs);
        sd.Free();
        return bSuccess;
    }

    /// <inheritdoc/>
    public bool GetString(out string stringData, short langId, byte stringIndex)
    {
        this.EnsureNotDisposed();
        this.EnsureOpen();

        stringData = null;
        int iTransferLength;
        LangStringDescriptor sd = new LangStringDescriptor(255);
        bool bSuccess = this.GetDescriptor((byte)DescriptorType.String, stringIndex, langId, sd.Ptr, sd.MaxSize, out iTransferLength);
        if (bSuccess && iTransferLength > UsbDescriptor.Size && sd.Length == iTransferLength)
        {
            bSuccess = sd.Get(out stringData);
        }

        return bSuccess;
    }

    /// <inheritdoc/>
    public void ResetDevice()
    {
        this.EnsureNotDisposed();
        this.EnsureOpen();

        NativeMethods.ResetDevice(this.deviceHandle).ThrowOnError();
    }

    /// <summary>
    /// Opens a device, allowing you to perform I/O on this device.
    /// </summary>
    public void Open()
    {
        this.OpenNative().ThrowOnError();
    }

    /// <inheritdoc/>
    public bool TryOpen()
    {
        return this.OpenNative() == Error.Success;
    }

    /// <summary>
    /// Closes the device after cancelling and draining asynchronous transfers.
    /// </summary>
    /// <remarks>最多等待 5 秒；超时保留句柄和事件线程，待最终回调后可重试 Close。禁止在原生事件线程调用。</remarks>
    public void Close()
    {
        originatingContext.EnsureNotEventThread();
        lock (originatingContext.DeviceSync)
        {
            this.EnsureNotDisposed();

            if (!this.IsOpen)
                return;

            bool shouldStopHandlingEvents = originatingContext.OpenDevices.Count == 1 && !originatingContext.IsUsingHotplug;

            if (!AsyncTransfer.CancelAndDrainAsync(this.deviceHandle).Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("USB transfers did not drain; the handle and event loop remain alive. Retry Close after completion.");

            this.deviceHandle.Dispose();
            this.deviceHandle = null;
            // claim 与 alt 缓存属于刚关闭的原生句柄，重开后必须重新 claim。
            this.mClaimedInterfaces.Clear();
            Array.Clear(this.usbAltInterfaceSettings, 0, this.usbAltInterfaceSettings.Length);

            if (shouldStopHandlingEvents)
                originatingContext.StopHandlingEvents();

            if (!originatingContext.IsDisposing)
                originatingContext.OpenDevices.Remove(this);
        }
    }

    /// <summary>
    /// Throws a <see cref="UsbException"/> if the device is not open.
    /// </summary>
    protected void EnsureOpen()
    {
        if (!this.IsOpen)
        {
            throw new UsbException("The device has not been opened. You need to call Open() first.");
        }
    }

    private Error OpenNative()
    {
        lock (originatingContext.DeviceSync)
        {
            this.EnsureNotDisposed();
            if (originatingContext.IsDisposing) throw new ObjectDisposedException(nameof(UsbContext));

            if (this.IsOpen)
            {
                return Error.Success;
            }

            var ret = originatingContext.OpenDevice(this.device, out var deviceHandle);

            if (ret == Error.Success)
            {
                this.deviceHandle = deviceHandle;
                this.descriptor = null;
                try
                {
                    AsyncTransfer.BindContext(this.deviceHandle, originatingContext);
                    // 轮询热插拔不会启动原生事件线程；打开设备时必须单独保证 I/O 完成通知运行。
                    originatingContext.StartHandlingEvents();
                    originatingContext.OpenDevices.Add(this);
                }
                catch
                {
                    this.deviceHandle.Dispose();
                    this.deviceHandle = null;
                    throw;
                }
            }

            return ret;
        }
    }
}
