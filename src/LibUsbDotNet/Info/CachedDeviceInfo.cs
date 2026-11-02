using LibUsbDotNet.LibUsb;

namespace LibUsbDotNet.Info;

public class CachedDeviceInfo
{
    private readonly int _deviceHashCode;

    public CachedDeviceInfo(UsbDevice device)
    {
        _deviceHashCode = device.GetHashCode();
        Descriptor = device.Descriptor;
        PortInfo = device.LocationId;
    }

    internal CachedDeviceInfo(UsbDeviceInfo descriptor, LocationId portInfo, int deviceHashCode)
    {
        Descriptor = descriptor;
        PortInfo = portInfo;
        _deviceHashCode = deviceHashCode;
    }

    public UsbDeviceInfo Descriptor { get; }
    public LocationId PortInfo { get; }

    protected bool Equals(CachedDeviceInfo other)
    {
        return GetHashCode() == other.GetHashCode();
    }

    public override int GetHashCode() => _deviceHashCode;
}
