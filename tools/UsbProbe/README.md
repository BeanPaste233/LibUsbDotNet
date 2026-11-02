# 通用只读 USB 探针

此工程不包含 DM 型号表、CAN、串口或固件协议。只读取枚举描述符并可选监听热插拔，不打开设备、不 claim 接口、不发送 bulk。适合先确认 libusb 动态库加载、枚举与监控生命周期；不能代替收发、占用、拔插与性能验收。

## 构建与运行

需要 .NET 8 或更高 SDK，以及与进程架构匹配的动态 libusb。Windows 使用 `libusb-1.0.dll`，Linux 使用 `libusb-1.0.so.0`，macOS 使用 `libusb-1.0.0.dylib`。Windows 本地诊断可把 DLL 放到输出目录；其它平台依照系统动态加载路径部署。不把原生库静态链接进本工程。

```powershell
dotnet build tools/UsbProbe/UsbProbe.csproj -c Release
Push-Location tools/UsbProbe/bin/Release/net8.0
try {
    dotnet UsbProbe.dll --help
    dotnet UsbProbe.dll --vid 0xFFFF --pid 0xFFFF --cycles 1000
    dotnet UsbProbe.dll --watch 10
} finally { Pop-Location }
```

VID/PID 参数是任意通用筛选条件；上面的 FFFF/FFFF 仅作通常无匹配设备的示例，不内置型号。`--watch` 是每轮监控秒数，`--cycles` 是 context 创建/枚举/释放次数；Ctrl+C 结束监控并释放资源。退出码：0 成功、1 运行失败、2 参数错误。

Windows 本地诊断从 DLL 所在的输出目录启动，核对 `Native library=` 的运行时版本；只核对磁盘文件版本不够。本机曾从仓库根启动时误加载 PATH 中 vcpkg 的 1.0.29，而非输出目录已放入的 1.0.30；本轮没有改系统 PATH 或生产库的加载策略。

`native_list_exit_control.py LIBUSB_PATH` 是绕过整个托管库的只读对照：一份列表保留、额外 8 轮枚举并逐份 free(unref=1)、最后释放首份列表再 exit，打印实际版本及热插拔能力。需要真实 Python 解释器与同架构动态库；不会打开设备。2026-10-20 Windows x64 对照：1.0.29 的三条 `still referenced` 可复现，官方 1.0.30 默认版同流程未复现，见 `tests/P0_USB_LIFECYCLE_TEST_REPORT.md`；不等于完整原生内存审计。

`after-dispose` 输出 GC 当前估算托管字节、进程 private bytes 和线程数，不强制 GC；仅供观察趋势，不能凭这些数值宣称无内存泄漏。监控错误或通知溢出使退出码非零。无匹配设备返回 0 只代表无设备路径正常，不代表硬件通过。

本轮仅本地研发验证，不提供正式可分发包；许可交付门槛见 `docs/P0_BASELINE.md`。
