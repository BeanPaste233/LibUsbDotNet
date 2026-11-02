# P0 通用 USB 生命周期测试报告

日期：2026-10-19，2026-10-20 补充 LinkX4C 真机。范围：`E:\CSharp_Projects\LibUsbDotNet`，基线 `e57f336baa1b36897767539878686954947dab95` + `chore/p0-usb-baseline` 当前未提交改动。

## 结论与边界

P0b 的本地离线生命周期回归通过；原生动态库加载、无匹配设备枚举和监控冒烟通过。2026-10-20 增加 Windows x64 / LinkX4C App 真机，实际异步短包、超时、取消、在途关闭、同对象重开、占用、多等待与两轮各 1000 次生命周期通过。**旧 1.0.29 的第三轮在途 IN 物理拔插/重连通过。随后升级官方 1.0.30，原生重复枚举引用警告在相同复现流程中未再出现，73/73 与额外 1000 次真机生命周期通过；新版一次人工拔插/重连补测也通过，退出无该引用警告。Linux 系统 1.0.25、macOS arm64 的 1.0.29 自动项目、各 1000 次生命周期及各一次人工 IN 在途拔插/重连也通过；新版 Linux 依赖与其它型号/场景仍未验收，P0 整体未完成。** 不把 fake 传输测试算成实际 USB 传输，也没有开始大规模 C# 设备协议重写。

没有提交、合并、推送、发布包或烧录固件。2026-10-20 已访问 Linux 和 macOS 台架并完成下文所列测试。独立包身份已配置；没有制作或发布正式 nupkg。正式分发的许可材料门槛仍见 `docs/P0_BASELINE.md`。

## 环境

- Windows `10.0.26200`，x64；`C:\Program Files\dotnet\dotnet.exe`，SDK 10.0.401；测试运行时 .NET 8.0.31。
- 库目标：`netstandard2.0`、`net8.0`、`net10.0`；测试目标 `net8.0`。
- 初始动态 libusb 文件版本 `1.0.29.11953`，来源 `D:\CLionProjects\dm_device_sdk_new\out\install\Release-MSVC\bin\libusb-1.0.dll`；当前诊断为 `1.0.30.12037`，升级来源与对应结果见后文。
- SHA256：`40FFBCE49F804D232691BEEE09FDA8CD9C7B8C980FD627CB56CC857DFFC1160C`。该 DLL 仅用于本机诊断，不静态链接。
- 故障注入使用实际可分配的 transfer 内存、fake 设备句柄与 native API。假设备指针不进入真实原生设备 I/O；UsbContext 初始化仍调用实际 libusb。

## 构建与接口身份

| 项目 | 结果 |
| --- | --- |
| 库 netstandard2.0 / net8.0 / net10.0 | 全部通过，0 编译错误 |
| `tools/UsbProbe` net8.0 | 通过 |
| PackageId | `BeanPaste.LibUsbDotNet` |
| 程序集、命名空间 | 保留 `LibUsbDotNet` |
| MSBuild GetBuildVersion 后包版本 | `3.0.249-ge57f336baa`；继承 NBGV 计算，不作为当前未提交改动的发布标识 |
| RepositoryUrl | 指向用户 BeanPaste233 fork |
| 源码格式 | 限定本次 .cs，dotnet format whitespace folder 模式检查通过；未运行 clang-format |
| diff 空白检查 | `git -c core.whitespace=cr-at-eol diff --check` 通过 |

原仓部分文件在 index 中本来就是 CRLF；裸 `git diff --check` 将新增 CR 误报尾空白。检查时只对本次命令启用 `cr-at-eol`，未改变全局配置或批量归一化行尾。构建仍有 NU1900：本机 NuGet local 源与 HTTP 缓存目录冲突；未删除缓存、修改全局源或关闭安全检查。

## 回归结果

2026-10-19 **68/68 通过**：原有原生初始化/版本/能力 4 项、AsyncTransfer/关闭 30 项、Hotplug 22 项、DeviceManager 12 项。2026-10-20 新增重开缓存、context/设备引用释放顺序和 Clone 拒绝共 5 项，当前 **73/73 通过**，重复 3 轮均通过。

| 范围 | 结果与证据 |
| --- | --- |
| 提交失败资源泄漏 | 首次测试失败复现 FreeCount=0；修复后资源计数平衡。`p0-submit-failure-before.trx` → `p0-transfer-after.trx` |
| 热插拔问题 | 最初 7 项先红，修复后通过。`p0-hotplug-before.trx` → `p0-hotplug-after.trx` |
| 取消/关闭状态机 | 新增 9 项先失败，修复后全部通过。`p0-cancel-before.trx` → `p0-cancel-after.trx` |
| 真实托管 Close/Dispose 顺序 | fake native 环境下 3 项先失败，接入排空与自等待防护后通过。`p0-close-before.trx` → `p0-close-after.trx` |
| 最终完整套件 | `p0-lifecycle-final.trx`，68/68 |
| 重复执行 | `p0-final-repeat-1.trx` 至 `p0-final-repeat-5.trx`，各 68/68 |

TRX 均位于忽略目录 `artifacts/test-results/`，不把生成产物混入源码提交。

覆盖要点：提前取消不分配、在途取消不提前 unpin、取消返回 Success/NotFound/Io 都等最终回调、取消抛异常不提前释放、完成/取消竞争 250 轮恰好一次回收、多请求排空、重复关闭、关闭后拒绝新提交、重开、context 排空、关闭一设备不影响另一设备、原生线程关闭拒绝、排空 5 秒超时保留资源且可重试。

热插拔与等待覆盖：同步 ENUMERATE、重复 arrival、未知 departure、注册失败回滚、普通通知容量 256 与溢出上报、慢/异常订阅、通知内注销/Dispose、250ms 轮询后端与枚举失败、1000 轮重复变化引用归还、指针 hash 冲突、同 finder 多等待、独立取消、稳定时间和总超时、重连序列、监控 generation、自定义 finder、原生事件循环失败唤醒等待。

## 只读探针实跑

命令均在 USB fork 根执行，筛选值 `FFFF/FFFF` 仅用于本次无匹配设备路径；探针不内置产品表，不打开/claim 任意外设：

```powershell
dotnet tools/UsbProbe/bin/Release/net8.0/UsbProbe.dll --help
dotnet tools/UsbProbe/bin/Release/net8.0/UsbProbe.dll --vid 0xFFFF --pid 0xFFFF --cycles 1000
dotnet tools/UsbProbe/bin/Release/net8.0/UsbProbe.dll --vid 0xFFFF --pid 0xFFFF --watch 2
dotnet tools/UsbProbe/bin/Release/net8.0/UsbProbe.dll --vid 0x10000
```

- help 正常；非法 VID 明确退出码 2。
- 1000 次 context 创建/枚举/释放：每轮匹配 0，最终 `DONE errors=0`，退出码 0；每 100 轮采样线程数均为 11。
- 第一轮 private bytes 16,408,576；第 500 轮 28,237,824；第 1000 轮 30,433,280。托管当前字节有 GC 起伏，第一轮 176,040、第 1000 轮 680,936。这只是进程趋势快照，**不能据此宣称无内存泄漏**；尚无设备打开/在途 I/O 的资源趋势数据。
- 2 秒无匹配设备监控和释放正常，`DONE errors=0`、退出码 0；不代表物理拔插已经验证。

## 2026-10-20 Windows LinkX4C 真机补充

设备 `34B7:4543`，实际为 App/L4 `V2.0.0 [c237c95 Oct 19 2026 12:07:12]`；配置 1、3 个 vendor 接口，无 CDC。PnP 缓存名称仍是 DM-CANFD-BOOT，不能据此判 boot。DM 协议仅位于独立设备探针 `E:\CSharp_Projects\DM_DeviceSDK\proto\LibUsbP0`，没有放进通用库；完整帧与固件报告在该仓库 `tests/P0_LIBUSB_LINKX4C_TEST_REPORT.md`。

- 实际接口 0/1/2 全部 claim；异步 bulk OUT/IN 版本查询返回 42/512 字节短包，长度和 CRC 正确。
- 50ms 空闲超时、提前和在途取消、取消后继续查询、4 个在途读取中 Close、同对象重开、context 在途 Dispose 后再释放完整枚举列表均通过。
- 真实子进程争用返回 Access，父连接可继续查询。实际初始到达通知 1 次、错误 0；两个独立等待成功，第三个取消互不影响；注销后返回引用仍可打开。
- 两轮各 1000 次 context/枚举/打开/claim/query/release/关闭/释放通过，用时约 21.44s / 21.13s。最终轮强制 GC 后：managed 170360→162336 B，private 23277568→27807744 B，handles 718→717，threads 18→16；没有随次数持续增长趋势，但不等同完整原生分配审计。
- 首次尝试物理拔插：挂读得到 Pipe，尚未等离线通知就失败退出，未捕获 DeviceLeft、没有人工操作时刻。该项不计通过，不把 Pipe 重写成 NoDevice；需按明确提示复测。
- 随后保持连接单独挂读 70 秒，再由 token 取消，后续查询正常（约 70001.43ms，证据 `p0-linkx4c-idle.log`）；未复现 Pipe，但第一次错误原因仍不明确。
- 第二轮用户明确确认拔插：UTC `02:23:06.9918548` 在途读返回 `(Pipe, 0)`，`02:23:07.0879250` 正常收到 DeviceLeft（约 96ms 后）。探针随后因要求 NoDevice 提前失败，未进入新到达等待，不能据此归因 DeviceManager。证据 `p0-linkx4c-unplug-retest.log`。
- 诊断探针改为保留原生错误，分别检查离线、旧句柄、新到达和重新查询；单独报告是否真的观察到 NoDevice，不把 Pipe 改映射为离线。本轮未修改库的 DeviceManager/错误映射。探针重新构建成功；自动真机冒烟通过，73/73 回归通过，证据 `p0-linkx4c-diagnostic-smoke.log`、`p0-unplug-diagnostic.trx`。
- 第三轮完整拔插/重连通过，日志 `p0-linkx4c-unplug-full.log`，退出 0。UTC `02:31:59.6191783` 返回 `(Pipe, 0)`，原生日志确认 completion errcode=31、`detected endpoint stall`；约 46.6ms 后收到 DeviceLeft。旧句柄写版本请求返回 Io，Close 成功。`02:32:12.1672257` 新到达，`02:32:12.6975484` 等待器返回新引用（设定稳定窗口 500ms），重新 claim 三接口并查询同构建、校验 CRC 成功。arrival=2、left=1、errors=0。传输没有返回 NoDevice，与重连链路通过分开记录；此次无证据支持继续重写 DeviceManager。
- 退出时有 `device 1.2/1.4/1.0 still referenced` 原生警告。单次枚举对照无警告，2 秒监控重复枚举可复现；绕过整个 C# 库，使用 `tools/UsbProbe/native_list_exit_control.py` 直接调用同一 DLL，首份列表保留、额外 8 次枚举/逐次 free(unref=1)、最后释放首份列表再 exit，仍复现同三条警告。证据 `p0-native-exit-control.log`、`p0-native-monitor-exit-control.log`、`p0-raw-native-list-exit-control.log`。具体根因/泄漏规模未确认，原生 DLL 未修改，不能称完整原生内存审计通过。

本轮发现两项真实问题并修复：成功 Close 清空旧 claim/alt 缓存，避免重开跳过原生 claim；每个设备 SafeHandle 保活原生 context，先归还设备再允许 libusb_exit，避免 context-first/list-second 的 UnrefDevice AV。补充已释放设备/context 的 Clone 前置检查。

证据 `artifacts/test-results/`：`p0-linkx4c-before.log`、`p0-linkx4c-after.log`（AV）、`p0-reopen-before/after.trx`、`p0-context-before/after.trx`、`p0-linkx4c-stress.log`、`p0-linkx4c-final.log`、`p0-linkx4c-manager.log`、`p0-linkx4c-unplug.log`、`p0-linkx4c-final.trx` 与 `p0-linkx4c-repeat-1.trx` 至 `-3.trx`。库三个目标重新构建和限定源码 whitespace 检查通过。

## 2026-10-20 原生 DLL 升至 1.0.30 对照

用户要求升级原生 DLL 后重试。原基线已是 1.0.29.11953，本轮使用 [官方 v1.0.30 发布包](https://github.com/libusb/libusb/releases/tag/v1.0.30) 的 `VS2022/MS64/dll/libusb-1.0.dll`，不使用 `MS64-hotplug` 实验构建，不修改原生源码。两版 `HAS_HOTPLUG=0`，仍走相同 250ms 轮询；不是切换监控后端规避问题。

- 归档 SHA256（与官方 release API digest 一致）：`7FB1DFEC805B97983763D7D0AE244320DA12ADD1003D4249C96CC4D586398C79`。
- 新 DLL 1.0.30.12037、166400 B，SHA256 `7CBF37E76DAE9C840C7E8DBF7348EE8897DCC86C8BA45E46ADA60B89411569F7`，位于 `artifacts/native/libusb-1.0.30/VS2022/MS64/dll/`。
- 旧版备份 `artifacts/native/libusb-1.0.29/libusb-1.0.dll`；原 C SDK 安装目录、系统 PATH、vcpkg 和正式分发产物均未修改。
- 仅替换通用探针、单测和 DM 探针的三个输出目录中的动态 DLL。诊断程序和版本单测增加运行时实际版本日志，不改生产库的加载策略、DeviceManager 或错误映射。

| 项目 | 结果与日志（均在 artifacts/test-results/） |
| --- | --- |
| 相同纯 native 复现 | 1.0.29 仍有三条 still referenced，1.0.30 无该警告；`p0-raw-native-1.0.29.log` / `p0-raw-native-1.0.30.log` |
| 托管监控 2 秒 | 实际 1.0.30.12037，arrival=1、errors=0、退出无该引用警告；`p0-native-1.0.30-monitor.log` |
| 回归套件 | 73/73，版本测试实际为 1.0.30.12037；`p0-native-1.0.30.trx` / `p0-native-1.0.30-console.log` |
| 真机自动冒烟及 1000 次生命周期 | 全部通过，约 21.00s；`p0-linkx4c-native-1.0.30.log`，被测固件/构建不变 |
| 新版人工 IN 在途拔插/重连 | 一次完整通过、退出 0，arrival=2、left=1、errors=0；`p0-linkx4c-native-1.0.30-unplug.log`，退出无 `still referenced` |

新版资源点 0→1000（强制 GC 后）：managed 179952→177000 B、private 23728128→28942336 B、handles 718→720、threads 18→17。窗口内没有持续增长趋势，不构成所有原生分配无泄漏的证明。与此问题无关的 HID 接口号警告仍存在。

加载路径曾造成一次无效升级验证：从仓库根执行时，虽然输出目录有新版，实际仍加载 PATH 中 vcpkg 的 1.0.29。该轮保留为 `p0-*-search-path-before.*`，不计新版结果；最终从各自输出目录启动，运行时版本日志均为 1.0.30.12037。此行为已记入两个探针 README，不改系统或生产查找策略。

新版人工拔插于 2026-10-20 UTC `03:16:27.9597486` 返回 `(Pipe, 0)`，原生日志仍为 completion errcode=31、endpoint stall；约 13.2ms 后收到 DeviceLeft。旧句柄写入拒绝为 Io，Close 正常。`03:16:44.4705307` 新到达，设定 500ms 稳定窗口后等待器约 501.1ms 返回新引用；重新 claim 0/1/2 并读取相同 App/L4 V2.0.0、相同构建、42 字节应答及正确 CRC。完整链路通过，未观察到 NoDevice，不改写原生错误；以上时间不是物理拔线到通知的精确延迟。

当前结果说明同一引用警告最小复现及本次拔插退出在 1.0.30 下均未再出现，不声称定位了具体上游修复提交或完成原生内存审计。其它平台/架构未复测；详细版本帧、拔插时间线与资源数据见 C# SDK 的 `tests/P0_LIBUSB_LINKX4C_TEST_REPORT.md`。

## 未覆盖项

2026-10-20 Linux 卸载 `dmcan` 后的新一轮拔插恢复 LinkX4C 枚举；不据此单独确认最初描述符错误根因。两个探针成功部署，系统 libusb 1.0.25.11696 下三接口、版本短包/CRC、超时、取消、在途关闭、重开、context 释放、多等待均通过；1000 次完整生命周期通过，5286.06ms。强制 GC 后 managed 168200→205608 B、private 129581056→152547328 B、handles 64→66、threads 14→14。一次人工 IN 在途拔插/重连通过，arrival=2、left=1、errors=0，退出 0；原生读 Io/0 B（URB -71）保留不改，旧句柄 NoDevice、新引用三接口与同构建/CRC 正常。首轮用户操作晚于等待上限不计库失败，成功结果来自重新挂读后的第二轮。≥1.0.29 复测待完成，实际争用 fixture 未实现、多设备未测。详见 [Linux 验收记录](P0_LINUX_USB_TEST_REPORT.md)。

macOS 同日经有线 `192.168.77.253` 完成部署和哈希核对。26.3 / arm64、实际加载 Homebrew libusb 1.0.29.11953；无匹配枚举 1000 次、三接口与相同固件版本/CRC、短包、超时、取消、四个 IN 在途 Close、同对象重开、context Dispose、多等待均通过，完整生命周期 1000 次 2813.76ms。强制 GC 后 managed 374040→368240 B、threads 17→17；private/handles 返回 0，不作为有效原生资源采样。人工 IN 在途拔插/重连一次通过，原生 Io / 0 B、旧句柄 NoDevice、关闭正常、新引用重新 claim 三接口并读取同构建/CRC；arrival=2、left=1、errors=0、退出 0。实际争用未测，详见 [macOS 记录](P0_MACOS_USB_TEST_REPORT.md)。

截至 2026-10-20，仅一台 LinkX4C App 已先后在 Windows/Linux/macOS 测试；下列均为**未测/未验收**：

- Windows 的其它型号、boot/CDC 形态、多个实体设备并发隔离、真正在途 OUT 拔插、大量反复拔插；已通过的是 LinkX4C 空闲 IN 挂读，1.0.29 与 1.0.30 各一次完整人工拔插/重连。
- 真正在途 OUT 的取消/关闭、多端点持续吞吐、CAN 高流量、调度延迟与更长期原生分配审计；本轮版本查询不能代替这些测试。
- Linux ≥1.0.29 原生库复测；Linux/macOS 实际占用和多设备；六 RID 原生库部署与设备兼容，macOS x64、有效原生资源采样及端点级取消的联动行为。
- 忘记 Dispose、进程终结、原生崩溃等进程级故障恢复，不作为本轮已验证项。

## 可复现命令

```powershell
dotnet build src/LibUsbDotNet/LibUsbDotNet.csproj -c Release --no-restore --nologo -v minimal
dotnet test src/LibUsbDotNet.Tests/LibUsbDotNet.Tests.csproj -c Release --no-restore --logger 'trx;LogFileName=p0-lifecycle-final.trx' --results-directory artifacts/test-results --nologo -v minimal
dotnet build tools/UsbProbe/UsbProbe.csproj -c Release --source https://api.nuget.org/v3/index.json --nologo -v minimal
dotnet msbuild src/LibUsbDotNet/LibUsbDotNet.csproj -t:GetBuildVersion -getProperty:PackageId,PackageVersion,NuGetPackageVersion,AssemblyName,RepositoryUrl -nologo
```

重复测试使用 `--no-build --no-restore`，日志名为 `p0-final-repeat-N.trx`；详细行为契约见 `docs/LIFECYCLE_AND_HOTPLUG.md`。硬件恢复后按本目录用例 B 逐平台验收，再决定是否进入大规模协议重写。
