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

using LibUsbDotNet.Info;
using LibUsbDotNet.Main;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace LibUsbDotNet.LibUsb;

/// <summary>
/// An instance of the libusb API. You can use multiple <see cref="UsbContext"/> which are independent
/// from each other.
/// </summary>
public class UsbContext : IUsbContext
{
    /// <summary>
    /// The native context.
    /// </summary>
    private readonly Context context;
    private readonly IUsbContextApi nativeApi;
    internal object DeviceSync { get; } = new();
    internal Error OpenDevice(Device device, out DeviceHandle handle) => nativeApi.Open(device, out handle);
    internal Device BindDevice(Device device) => new ContextDevice(device, context);

    /// <summary>原生事件线程必须继续派发最终回调，不能同步等待自身排空。</summary>
    internal void EnsureNotEventThread()
    {
        if (Thread.CurrentThread == eventHandlingThread)
            throw new InvalidOperationException("Cannot close USB devices or their context from the USB event thread.");
    }
    private readonly object eventThreadSync = new();
    private readonly object hotplugSync = new();
    private readonly DeviceEventQueue notifications = new();
    private readonly Dictionary<IntPtr, HotplugDevice> hotplugDevices = new();
    private TaskCompletionSource<bool> hotplugChanged = NewChangeSource();
    private Timer hotplugTimer;
    private bool nativeHotplug;
    private long hotplugGeneration;
    private long arrivalSequence;
    private HotplugOptions activeHotplugOptions;

    internal sealed class HotplugDevice
    {
        internal IntPtr Id { get; init; }
        internal UsbDevice Device { get; init; }
        internal CachedDeviceInfo Info { get; init; }
        internal long Arrival { get; init; }
    }

    private static TaskCompletionSource<bool> NewChangeSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Thread for event handling.
    /// </summary>
    private Thread eventHandlingThread;

    /// <summary>
    /// ID of the underlying <see cref="Context"/>.
    /// </summary>
    public string HandleId => context.ToString();

    /// <summary>
    /// Tracks whether this context has been disposed of, or not.
    /// </summary>
    public bool IsDisposed { get; private set; }

    public bool IsUsingHotplug { get; private set; }

    public HotplugOptions HotplugOptions { get; init; } = new();

    /// <summary>
    /// Tracking list of all devices that are open on this context.
    /// </summary>
    internal List<UsbDevice> OpenDevices { get; }

    /// <summary>
    /// Read-only list of all devices that are open on this context.
    /// </summary>
    public ReadOnlyCollection<UsbDevice> ReadOnlyOpenDevices
    {
        get { lock (DeviceSync) return new ReadOnlyCollection<UsbDevice>(OpenDevices.ToArray()); }
    }

    /// <summary>
    /// Tracks when the context is currently being disposed.
    /// <remarks>
    /// Used for preventing the devices in <see cref="OpenDevices"/> from modifying the list when they are closed in <see cref="Dispose()"/>.
    /// </remarks>
    /// </summary>
    internal bool IsDisposing { get; private set; }

    /// <summary>
    /// Allows the event handling thread to return when set to 1.
    /// </summary>
    internal int stopHandlingEvents;

    private readonly IntPtr hotplugDelegatePtr;
    private readonly HotplugCallbackFn hotplugCallback;
    public delegate void DeviceEventHandler(object sender, DeviceEventArgs e);
    /// <summary>顺序异步通知；到达事件中的设备仅在回调期间借用，需要保留时请 Clone。</summary>
    public event EventHandler<DeviceEventArgs> DeviceEvent;
    /// <summary>通知订阅者异常或监控错误；此事件自身的异常仅写入 Trace。</summary>
    public event EventHandler<UnhandledExceptionEventArgs> DeviceEventError;
    internal ConcurrentDictionary<UsbDevice, CachedDeviceInfo> DeviceInfoDictionary { get; } = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="UsbContext"/> class.
    /// </summary>
    public UsbContext() : this(UsbContextApi.Instance)
    {
    }

    internal UsbContext(IUsbContextApi nativeApi)
    {
        this.nativeApi = nativeApi;
        notifications.ErrorHandler = ReportDeviceEventError;
        IntPtr contextHandle = IntPtr.Zero;
        NativeMethods.Init(ref contextHandle).ThrowOnError();
        this.context = Context.DangerousCreate(contextHandle);
        OpenDevices = new List<UsbDevice>();
        hotplugCallback = new HotplugCallbackFn(HotplugCallback);
        hotplugDelegatePtr = Marshal.GetFunctionPointerForDelegate(hotplugCallback);
    }

    ~UsbContext()
    {
        // Put cleanup code in Dispose(bool disposing).
        this.Dispose(false);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // Put cleanup code in Dispose(bool disposing).
        this.Dispose(true);

        GC.SuppressFinalize(this);
    }

    /// <inheritdoc/>
    public void SetDebugLevel(LogLevel level)
    {
        NativeMethods.SetDebug(this.context, (int)level);
    }

    private int HotplugCallback(IntPtr ctx, IntPtr device, HotplugEvent hotplugEvent, IntPtr userData)
    {
        try
        {
            lock (hotplugSync)
            {
                if (!IsUsingHotplug) return 0;
                if (hotplugEvent == HotplugEvent.DeviceArrived) AddHotplugDevice(device);
                else if (hotplugEvent == HotplugEvent.DeviceLeft) RemoveHotplugDevice(device);
            }
        }
        catch (Exception ex)
        {
            notifications.Post(() => ReportDeviceEventError(ex));
        }
        return 0;
    }

    /// <summary>注册热插拔监控；无原生支持时，每 250 ms 枚举一次，不产生设备 I/O。</summary>
    public void RegisterHotPlug()
    {
        lock (DeviceSync)
        {
            lock (hotplugSync)
            {
                if (IsDisposed || IsDisposing) throw new ObjectDisposedException(nameof(UsbContext));
                if (IsUsingHotplug) return;
                if (HotplugOptions == null) throw new ArgumentNullException(nameof(HotplugOptions));
                if (HotplugOptions.VendorId < -1 || HotplugOptions.VendorId > ushort.MaxValue ||
                    HotplugOptions.ProductId < -1 || HotplugOptions.ProductId > ushort.MaxValue ||
                    HotplugOptions.DeviceClass < -1 || HotplugOptions.DeviceClass > byte.MaxValue ||
                    HotplugOptions.HotplugEventFlags == 0 ||
                    (HotplugOptions.HotplugEventFlags & ~(HotplugEvent.DeviceArrived | HotplugEvent.DeviceLeft)) != 0)
                    throw new ArgumentOutOfRangeException(nameof(HotplugOptions));
                activeHotplugOptions = new HotplugOptions
                {
                    VendorId = HotplugOptions.VendorId,
                    ProductId = HotplugOptions.ProductId,
                    DeviceClass = HotplugOptions.DeviceClass,
                    HotplugEventFlags = HotplugOptions.HotplugEventFlags
                };
                IsUsingHotplug = true; // ENUMERATE 可能在 Register 返回前同步通知。
                hotplugGeneration++;
                bool registered = false;
                try
                {
                    nativeHotplug = nativeApi.SupportsHotplug;
                    if (nativeHotplug)
                    {
                        nativeApi.Register(context, activeHotplugOptions, hotplugDelegatePtr, ref HotplugOptions.Handle).ThrowOnError();
                        registered = true;
                    }
                    else
                    {
                        PollHotplug();
                        hotplugTimer = new Timer(_ => PollHotplug(), null, 250, 250);
                    }
                }
                catch
                {
                    IsUsingHotplug = false;
                    hotplugGeneration++;
                    if (registered) nativeApi.Deregister(context, HotplugOptions.Handle);
                    ClearHotplugDevices();
                    throw;
                }
            }
            // 不在 hotplugSync 内等待旧事件线程，旧线程可能正等待进入热插拔回调。
            if (nativeHotplug)
            {
                try { StartHandlingEvents(); }
                catch { UnregisterHotPlug(); throw; }
            }
        }
    }

    /// <summary>注销监控，不中止打开设备的异步 I/O；已在执行的用户通知可以完成。</summary>
    public void UnregisterHotPlug()
    {
        lock (DeviceSync)
        {
            lock (hotplugSync)
            {
                if (!IsUsingHotplug) return;
                IsUsingHotplug = false;
                hotplugGeneration++;
                hotplugTimer?.Dispose();
                hotplugTimer = null;
                if (nativeHotplug) nativeApi.Deregister(context, HotplugOptions.Handle);
                ClearHotplugDevices();
            }
            if (OpenDevices.Count == 0) StopHandlingEvents();
        }
    }

    private void SignalHotplugChanged()
    {
        var previous = hotplugChanged;
        hotplugChanged = NewChangeSource();
        previous.TrySetResult(true);
    }

    internal (HotplugDevice[] Devices, Task Changed, long Sequence, long Generation, bool Active) GetHotplugSnapshot()
    {
        lock (hotplugSync)
            return (hotplugDevices.Values.ToArray(), hotplugChanged.Task, arrivalSequence, hotplugGeneration,
                IsUsingHotplug && (!nativeHotplug || Volatile.Read(ref stopHandlingEvents) == 0));
    }

    internal UsbDevice CloneHotplugDevice(HotplugDevice entry)
    {
        lock (hotplugSync)
        {
            return IsUsingHotplug && hotplugDevices.TryGetValue(entry.Id, out var current) && ReferenceEquals(entry, current)
                ? nativeApi.RetainDevice(entry.Id, this) : null;
        }
    }

    private void AddHotplugDevice(IntPtr id)
    {
        if (hotplugDevices.ContainsKey(id)) return; // ENUMERATE 与实际插入通知可能重复。
        var device = nativeApi.RetainDevice(id, this);
        try
        {
            var info = nativeApi.GetDeviceInfo(device);
            var entry = new HotplugDevice { Id = id, Device = device, Info = info, Arrival = ++arrivalSequence };
            hotplugDevices.Add(id, entry);
            DeviceInfoDictionary.TryAdd(device, info);
            SignalHotplugChanged();
        }
        catch
        {
            device.Dispose();
            throw;
        }
        if (DeviceEvent != null && (activeHotplugOptions.HotplugEventFlags & HotplugEvent.DeviceArrived) != 0)
        {
            var notificationDevice = nativeApi.RetainDevice(id, this);
            QueueDeviceEvent(new DeviceArrivedEventArgs(notificationDevice), notificationDevice);
        }
    }

    private void RemoveHotplugDevice(IntPtr id)
    {
        if (!hotplugDevices.TryGetValue(id, out var entry)) return; // libusb 允许没有到达记录的离线通知。
        hotplugDevices.Remove(id);
        DeviceInfoDictionary.TryRemove(entry.Device, out _);
        entry.Device.Dispose();
        SignalHotplugChanged();
        if (DeviceEvent != null && (activeHotplugOptions.HotplugEventFlags & HotplugEvent.DeviceLeft) != 0)
            QueueDeviceEvent(new DeviceLeftEventArgs(entry.Info));
    }

    private void ClearHotplugDevices()
    {
        foreach (var entry in hotplugDevices.Values) entry.Device.Dispose();
        hotplugDevices.Clear();
        DeviceInfoDictionary.Clear();
        SignalHotplugChanged();
    }

    private void QueueDeviceEvent(DeviceEventArgs args, UsbDevice device = null)
    {
        long generation = hotplugGeneration;
        bool retainedContext = false;
        bool posted;
        try
        {
            context.DangerousAddRef(ref retainedContext);
            posted = notifications.Post(() =>
            {
                try
                {
                    lock (hotplugSync)
                    {
                        if (!IsUsingHotplug || generation != hotplugGeneration) return;
                    }
                    var handlers = DeviceEvent;
                    if (handlers == null) return;
                    foreach (EventHandler<DeviceEventArgs> handler in handlers.GetInvocationList())
                    {
                        if (!IsUsingHotplug || generation != Volatile.Read(ref hotplugGeneration)) break;
                        try { handler(this, args); }
                        catch (Exception ex) { ReportDeviceEventError(ex); }
                    }
                }
                finally
                {
                    try { device?.Dispose(); }
                    finally { context.DangerousRelease(); }
                }
            });
        }
        catch
        {
            try { device?.Dispose(); }
            finally { if (retainedContext) context.DangerousRelease(); }
            throw;
        }
        if (!posted)
        {
            try { device?.Dispose(); }
            finally { context.DangerousRelease(); }
        }
    }

    private void ReportDeviceEventError(Exception error)
    {
        Trace.TraceError("USB hotplug error: {0}", error);
        var handlers = DeviceEventError;
        if (handlers == null) return;
        foreach (EventHandler<UnhandledExceptionEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, new UnhandledExceptionEventArgs(error, false)); }
            catch (Exception ex) { Trace.TraceError("USB error handler failed: {0}", ex); }
        }
    }

    private void PollHotplug()
    {
        if (!Monitor.TryEnter(hotplugSync)) return;
        try
        {
            if (!IsUsingHotplug || nativeHotplug) return;
            using var devices = nativeApi.List(this);
            var present = new HashSet<IntPtr>();
            foreach (UsbDevice device in devices)
            {
                var info = nativeApi.GetDeviceInfo(device).Descriptor;
                if ((activeHotplugOptions.VendorId != -1 && activeHotplugOptions.VendorId != info.VendorId) ||
                    (activeHotplugOptions.ProductId != -1 && activeHotplugOptions.ProductId != info.ProductId) ||
                    (activeHotplugOptions.DeviceClass != -1 && activeHotplugOptions.DeviceClass != info.DeviceClass)) continue;
                present.Add(device.NativeDeviceId);
                AddHotplugDevice(device.NativeDeviceId);
            }
            foreach (var missing in hotplugDevices.Keys.Where(id => !present.Contains(id)).ToArray())
                RemoveHotplugDevice(missing);
        }
        catch (Exception ex) { notifications.Post(() => ReportDeviceEventError(ex)); }
        finally { Monitor.Exit(hotplugSync); }
    }

    /// <summary>
    /// Returns a list of USB devices currently attached to the system.
    /// </summary>
    /// <returns>
    /// A <see cref="UsbDeviceCollection"/> which contains the devices currently
    /// attached to the system.</returns>
    /// <remarks>
    /// <para>
    /// This is your entry point into finding a USB device to operate.
    /// </para>
    /// <para>
    /// You are expected to dispose all the devices once you are done with them. Disposing the <see cref="UsbDeviceCollection"/>
    /// will dispose all devices in that collection. You can <see cref="UsbDevice.Clone"/> a device to get a copy of the device
    /// which you can use after you've disposed the <see cref="UsbDeviceCollection"/>.
    /// </para>
    /// </remarks>
    public unsafe UsbDeviceCollection List()
    {
        if (IsDisposed || IsDisposing) throw new ObjectDisposedException(nameof(UsbContext));
        IntPtr* list = null;
        var deviceCount = NativeMethods.GetDeviceList(this.context, &list);
        if (deviceCount.ToInt64() < 0) throw new UsbException((Error)deviceCount.ToInt32());
        Collection<IUsbDevice> devices = new Collection<IUsbDevice>();
        try
        {
            for (int i = 0; i < deviceCount.ToInt64(); i++)
            {
                // 返回集合取得独立引用；原生列表无论成功或异常都统一释放。
                devices.Add(nativeApi.RetainDevice(list[i], this));
            }
            return new UsbDeviceCollection(devices);
        }
        catch
        {
            foreach (var device in devices) device.Dispose();
            throw;
        }
        finally { NativeMethods.FreeDeviceList(list, unrefDevices: 1); }
    }

    /// <inheritdoc/>
    public IUsbDevice Find(UsbDeviceFinder finder)
    {
        if (finder == null)
        {
            throw new ArgumentNullException(nameof(finder));
        }

        return this.Find(finder.Check);
    }

    /// <inheritdoc/>
    public IUsbDevice Find(Func<IUsbDevice, bool> predicate)
    {
        if (predicate == null)
        {
            throw new ArgumentNullException(nameof(predicate));
        }

        using (var list = this.List())
        {
            foreach (var device in list)
            {
                if (predicate(device))
                {
                    return device.Clone();
                }
            }
        }

        return null;
    }

    /// <inheritdoc/>
    public UsbDeviceCollection FindAll(UsbDeviceFinder finder)
    {
        if (finder == null)
        {
            throw new ArgumentNullException(nameof(finder));
        }

        return this.FindAll(finder.Check);
    }

    /// <inheritdoc/>
    public UsbDeviceCollection FindAll(Func<IUsbDevice, bool> predicate)
    {
        if (predicate == null)
        {
            throw new ArgumentNullException(nameof(predicate));
        }

        var matchingDevices = new List<IUsbDevice>();

        using (var list = this.List())
        {
            foreach (var device in list)
            {
                if (predicate(device))
                {
                    matchingDevices.Add(device.Clone());
                }
            }
        }

        UsbDeviceCollection devices = new UsbDeviceCollection(matchingDevices);
        return devices;
    }

    /// <summary>
    /// Starts the event handling thread.
    /// </summary>
    public void StartHandlingEvents()
    {
        lock (eventThreadSync)
        {
            if (IsDisposed || IsDisposing) throw new ObjectDisposedException(nameof(UsbContext));
            if (eventHandlingThread?.IsAlive == true)
            {
                if (Volatile.Read(ref stopHandlingEvents) == 0) return;
                if (Thread.CurrentThread == eventHandlingThread)
                    throw new InvalidOperationException("Cannot restart the USB event loop from its own thread.");
                eventHandlingThread.Join();
            }
            stopHandlingEvents = 0;
            eventHandlingThread = new Thread(HandleEvents) { IsBackground = true, Name = "LibUsb events" };
            eventHandlingThread.Start();
        }
    }

    /// <summary>
    /// Attempts to stop the event handling thread.
    /// </summary>
    /// <remarks>
    /// 唤醒空闲的原生等待。不能从原生事件线程等待自身退出。
    /// </remarks>
    public void StopHandlingEvents()
    {
        Thread thread;
        lock (eventThreadSync)
        {
            thread = eventHandlingThread;
            if (thread == null) return;
            if (thread == Thread.CurrentThread)
                throw new InvalidOperationException("Cannot join the USB event loop from its own thread.");
            Interlocked.Exchange(ref stopHandlingEvents, 1);
            nativeApi.Interrupt(context);
        }
        thread.Join();
        lock (eventThreadSync)
        {
            if (eventHandlingThread == thread) eventHandlingThread = null;
        }
        lock (hotplugSync)
        {
            if (IsUsingHotplug && nativeHotplug)
            {
                hotplugGeneration++;
                SignalHotplugChanged();
            }
        }
    }

    protected virtual void Dispose(bool disposeManagedObjects)
    {
        EnsureNotEventThread();
        lock (hotplugSync)
        {
            if (IsDisposed || IsDisposing || context == null) return;
            IsDisposing = true;
        }

        // Not sure what should go here, what resources should be cleaned up by explicit Dispose but not in the finalizer?
        if (disposeManagedObjects)
        {
            // Dispose managed state (managed objects).
        }

        try
        {
            // Close any devices still open on this context before stopping their completion loop.
            UnregisterHotPlug();
            lock (DeviceSync)
            {
                foreach (var openDevice in OpenDevices.ToArray()) openDevice.Dispose();
                OpenDevices.Clear();
            }
            StopHandlingEvents();
            notifications.Drain();

            // Dispose of underlying context handle only after every device has drained.
            context.Dispose();
            IsDisposed = true;
        }
        catch
        {
            lock (hotplugSync) IsDisposing = false;
            throw;
        }
    }

    private void HandleEvents()
    {
        try
        {
            while (Volatile.Read(ref stopHandlingEvents) == 0)
            {
                var error = nativeApi.HandleEvents(this.context, ref stopHandlingEvents);
                if (error == Error.Interrupted)
                {
                    continue;
                }

                if (error != Error.Success)
                {
                    throw new UsbException(error);
                }
            }
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref stopHandlingEvents, 1);
            lock (hotplugSync)
            {
                hotplugGeneration++;
                SignalHotplugChanged();
            }
            notifications.Post(() => ReportDeviceEventError(ex));
        }
    }
}
