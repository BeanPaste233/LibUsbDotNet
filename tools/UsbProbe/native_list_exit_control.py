"""绕过托管库，复现重复枚举后 libusb_exit 的原生引用警告；不打开设备。"""

import ctypes
import sys

if len(sys.argv) != 2:
    raise SystemExit("Usage: native_list_exit_control.py LIBUSB_PATH")

usb = ctypes.CDLL(sys.argv[1])
pointer = ctypes.c_void_p
items = ctypes.POINTER(pointer)
usb.libusb_init.argtypes = [ctypes.POINTER(pointer)]
usb.libusb_get_device_list.argtypes = [pointer, ctypes.POINTER(items)]
usb.libusb_get_device_list.restype = ctypes.c_ssize_t
usb.libusb_free_device_list.argtypes = [items, ctypes.c_int]
usb.libusb_set_debug.argtypes = [pointer, ctypes.c_int]
usb.libusb_exit.argtypes = [pointer]
usb.libusb_get_version.restype = ctypes.POINTER(ctypes.c_uint16 * 4)
usb.libusb_has_capability.argtypes = [ctypes.c_uint32]
print("native-version=" + ".".join(map(str, usb.libusb_get_version().contents)), flush=True)
print("native-hotplug=" + str(usb.libusb_has_capability(1)), flush=True)

context = pointer()
assert usb.libusb_init(ctypes.byref(context)) == 0
initial = items()
try:
    usb.libusb_set_debug(context, 2)
    assert usb.libusb_get_device_list(context, ctypes.byref(initial)) >= 0
    for cycle in range(8):
        current = items()
        count = usb.libusb_get_device_list(context, ctypes.byref(current))
        assert count >= 0
        usb.libusb_free_device_list(current, 1)
finally:
    if initial:
        usb.libusb_free_device_list(initial, 1)
    print("RAW_NATIVE_EXIT: all device lists freed with unref=1", flush=True)
    usb.libusb_exit(context)
print("RAW_NATIVE_DONE", flush=True)
