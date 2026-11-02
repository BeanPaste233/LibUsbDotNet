// SPDX-FileCopyrightText: 2026 BeanPaste233
// SPDX-License-Identifier: LGPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using LibUsbDotNet.LibUsb;

namespace UsbProbe;

/// <summary>通用只读 USB 探针：不打开设备，不 claim 接口，不发送设备协议。</summary>
internal static class Program
{
    /// <summary>枚举与可选热插拔监控；退出码 0 成功、1 运行失败、2 参数错误。</summary>
    private static async Task<int> Main(string[] args)
    {
        int vid = -1, pid = -1, cycles = 1, watchSeconds = 0;
        try
        {
            for (int index = 0; index < args.Length; index++)
            {
                if (args[index] == "--help")
                {
                    Console.WriteLine("UsbProbe [--vid ID] [--pid ID] [--cycles N] [--watch SECONDS]");
                    Console.WriteLine("IDs: decimal or 0xHEX. Read-only; no open, claim, bulk I/O or device protocol.");
                    return 0;
                }
                string option = args[index];
                if (++index >= args.Length) throw new ArgumentException($"Missing value for {option}.");
                string text = args[index];
                int value = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? int.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    : int.Parse(text, CultureInfo.InvariantCulture);
                switch (option)
                {
                    case "--vid" when value is >= 0 and <= 65535: vid = value; break;
                    case "--pid" when value is >= 0 and <= 65535: pid = value; break;
                    case "--cycles" when value is > 0 and <= 100000: cycles = value; break;
                    case "--watch" when value is > 0 and <= 3600: watchSeconds = value; break;
                    default: throw new ArgumentException($"Invalid option/value: {option} {text}.");
                }
            }
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        try
        {
            Console.WriteLine($"OS={RuntimeInformation.OSDescription} arch={RuntimeInformation.ProcessArchitecture} runtime={Environment.Version}");
            Console.WriteLine($"Managed library={typeof(UsbContext).Assembly.GetName().Version}; cycles={cycles}; watch={watchSeconds}s");
            Console.WriteLine($"Native library={NativeLibraryVersion.FullVersion}");
            using var stop = new CancellationTokenSource();
            ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
            Console.CancelKeyPress += onCancel;
            int errors = 0;
            try
            {
                for (int cycle = 1; cycle <= cycles && !stop.IsCancellationRequested; cycle++)
                {
                    using (var context = new UsbContext
                    {
                        HotplugOptions = new HotplugOptions { VendorId = vid, ProductId = pid }
                    })
                    {
                        using var devices = context.List();
                        int matched = 0;
                        foreach (var device in devices)
                        {
                            if ((vid != -1 && device.VendorId != vid) || (pid != -1 && device.ProductId != pid)) continue;
                            matched++;
                            if (cycle == 1) Console.WriteLine($"device vid=0x{device.VendorId:X4} pid=0x{device.ProductId:X4} location={device.LocationId}");
                        }
                        if (cycle == 1 || cycle % 100 == 0 || cycle == cycles)
                            Console.WriteLine($"cycle={cycle} matched={matched}");
                        if (watchSeconds > 0)
                        {
                            context.DeviceEventError += (_, e) =>
                            {
                                Interlocked.Increment(ref errors);
                                Console.Error.WriteLine($"hotplug-error={e.ExceptionObject}");
                            };
                            context.DeviceEvent += (_, e) =>
                            {
                                string description = e switch
                                {
                                    DeviceArrivedEventArgs arrived => $"arrived vid=0x{arrived.Device.VendorId:X4} pid=0x{arrived.Device.ProductId:X4} location={arrived.Device.LocationId}",
                                    DeviceLeftEventArgs left => $"left vid=0x{left.DeviceInfo.Descriptor.VendorId:X4} pid=0x{left.DeviceInfo.Descriptor.ProductId:X4} location={left.DeviceInfo.PortInfo}",
                                    _ => e.GetType().Name
                                };
                                Console.WriteLine($"{DateTimeOffset.UtcNow:O} {description}");
                            };
                            context.RegisterHotPlug();
                            try { await Task.Delay(TimeSpan.FromSeconds(watchSeconds), stop.Token); }
                            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                        }
                    }
                    if (cycle == 1 || cycle % 100 == 0 || cycle == cycles)
                    {
                        using var process = Process.GetCurrentProcess();
                        Console.WriteLine($"after-dispose cycle={cycle} managed={GC.GetTotalMemory(false)} private={process.PrivateMemorySize64} threads={process.Threads.Count}");
                    }
                }
            }
            finally { Console.CancelKeyPress -= onCancel; }
            Console.WriteLine($"DONE errors={errors}. Hardware I/O and unplug tests are NOT covered by this probe.");
            return errors == 0 ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAILED: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }
}
