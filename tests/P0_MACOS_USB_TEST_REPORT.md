# P0 macOS USB 验收记录

日期：2026-10-20。范围：当前 LibUsbDotNet fork 未提交 P0 改动，不是 C SDK 的历史 macOS 结果。

## 当前结论

**macOS arm64 / LinkX4C 自动项目、1000 次完整生命周期和一次人工 IN 在途拔插/重连通过，三项探针均退出 0。** 实际加载 libusb 1.0.29.11953。实际争用、多实体设备隔离、其它型号/boot/CDC、OUT 在途、持续吞吐及原生内存审计未测；P0 整体仍未验收。

## 环境与部署

- 有线主机：`dmdlmac@192.168.77.253`，`dmdl-macdeMac-mini.local`，macOS 26.3 / 25D125，arm64；旧无线地址 `192.168.77.187`。SSH 使用 `HostKeyAlias=192.168.77.187` 核对既有主机密钥，未关闭校验。
- USB：`34B7:4543`，DM-LinkX4C，位置 `2-1.1.3`（IORegistry `0x02113000`）；App/L4 `V2.0.0 [c237c95 Oct 19 2026 12:07:12]`，与本轮 Windows/Linux 被测构建一致。
- 原生库：Homebrew `/opt/homebrew/Cellar/libusb/1.0.29/lib/libusb-1.0.0.dylib`，运行日志确认 `native=1.0.29.11953`；没有替换系统库。
- USB fork：`chore/p0-usb-baseline`，基线 `e57f336baa1b36897767539878686954947dab95` + 未提交 P0 改动。
- 探针：osx-arm64 / .NET 8.0.31 自包含；设备探针独立位于 C# SDK 的 `proto/LibUsbP0`，只发送版本查询，通用库不内置 DM 协议。
- 两套 `LibUsbDotNet.dll` SHA256 均为 `0887A42A34553A186D2E1E669EFE78143E8B5982692F0BC08306E85566B524C5`，与 Windows/Linux 相同。
- 归档 `artifacts/macos-p0/probes-osx-arm64.tgz`：60098828 B，SHA256 `5B1F1849376A8E76FEA1E648FFEE34E2A2247641B10BAE2A29FAB880D2D62623`。
- 无线 SSH/SCP 曾多次中断，标准 SFTP `reput` 续传完成；13:45:16（北京时间）通过有线连接核对远端归档大小/哈希一致后才解包。远端隔离目录 `/Users/dmdlmac/libusbdotnet-p0-20261020`，没有覆盖旧 SDK 或用户工程。
- 原有 NU1900 缓存路径警告保留，未删除全局缓存或关闭漏洞检查；交叉发布退出 0。

## 自动项目

| 项目 | 结果 |
| --- | --- |
| 无匹配设备枚举/释放 1000 次 | matched=0、DONE errors=0、probe-exit=0 |
| 描述符与 claim | 实际接口 0/1/2 全 vendor，各 bulk 最大包 512 B；不假定 CDC 数量 |
| 异步版本查询短包 | OUT 7 B、IN 42/512 B，长度/CRC/软件类型/型号/版本/构建正确 |
| 空闲超时 50ms | Timeout / 0 B，实测 56.54ms |
| 提前取消、在途 token 取消 | 标准取消异常；在途取消 0.94ms，随后版本查询正常 |
| 四个在途 IN 中 Close | 四个结果均 Io / 0 B，2.16ms；重复 Close 正常 |
| 同对象重开 | 重新 claim 并读回相同版本/构建/CRC |
| 在途 context Dispose | 1.26ms，读取消，后续列表与设备释放未崩溃 |
| 初始缓存、多等待及独立取消 | arrival=1、errors=0；注销后返回引用仍可打开/查询 |
| 完整生命周期 1000 次 | 2813.76ms；每轮新 context/枚举/打开/claim/query/release/关闭/释放，probe-exit=0 |

版本请求与应答：

```text
OUT A5060000E0015A
IN  A506002200414C3402000063323337633935204F637420313920323032362031323A30373A3132B6085A
```

强制 GC 后资源：cycle 0→1000，managed 374040→368240 B，threads 17→17。当前探针在本 macOS 上的 private/handles 均返回 0，视为无有效采样，**不能解释成零内存、零句柄或原生无泄漏**。无匹配枚举的 managed 未强制 GC，不能与上述存活堆直接比较。

## 人工 IN 在途拔插

探针于北京时间 13:46:46 输出 `READY_UNPLUG` 后才请用户操作。用户确认已插拔；以下为日志 UTC 时间，不是物理拔线时刻的精确测量。

| UTC 时间 | 事件 |
| --- | --- |
| 05:47:42.2199270 | 在途 IN 返回 Io / 0 B，保留原生错误；未观察到传输 NoDevice |
| 05:47:42.2348080 | DeviceLeft，晚于读完成约 14.9ms |
| 05:47:42.2392900 | 旧句柄写入拒绝，NoDevice |
| 05:47:42.2421780 | 旧句柄 Close 正常完成 |
| 05:47:48.0030770 | 同位置 DeviceArrived |
| 05:47:48.5067590 | DeviceManager 返回新引用；设定 500ms 稳定窗口，实际约 503.7ms |

新引用重新 claim 0/1/2，读取相同固件构建与正确 CRC。最终 `unplug-reconnect=PASS arrivals=2 departures=1 errors=0; in-flight-native-error=Io`、`probe-exit=0`。拔出后原生层有 IOService 不再连接的写入/关闭告警，与此时设备已移除一致；未将 Io 改映射为 NoDevice。退出无 `still referenced`，不据此宣称完成原生泄漏审计。

第一次拔插启动在版本初始化前因未找到 dylib 退出 1，没有设备 I/O，也未提示用户拔插；保留 `*-unplug.log`。改为与自动 runner 一样，在 shell 脚本内部 export 进程级 `DYLD_FALLBACK_LIBRARY_PATH=/opt/homebrew/lib` 后，`*-unplug-retry1.log` 完整通过。未修改库加载策略或系统配置，不覆盖失败证据。

## 复现与原始证据

远端隔离目录中：

```sh
export DYLD_FALLBACK_LIBRARY_PATH=/opt/homebrew/lib
./UsbProbe/UsbProbe --vid 0xFFFF --pid 0xFFFF --cycles 1000
./LibUsbP0/LibUsbP0 --pid 0x4543 --cycles 1000
./LibUsbP0/LibUsbP0 --pid 0x4543 --unplug
```

实际通过 `nohup /bin/sh run-macos-p0.sh`、`nohup /bin/sh run-macos-unplug.sh` 后台执行，日志在 Mac 本机落盘。原始日志已下载到本仓库 `artifacts/test-results/`，SHA256 与远端逐份一致：

| 日志 | SHA256 |
| --- | --- |
| macos-no-match-libusb-1.0.29.log | 6DB66172567018201F5C0BCE239F3711D2E470CE6CF8EE840ACC6705610C6F07 |
| macos-linkx4c-libusb-1.0.29.log | DE43EFB839D3A2822CAF1B23F6F9C625E6C7D8A32D5A9487804EF1A8F7B76569 |
| macos-linkx4c-libusb-1.0.29-unplug-retry1.log | 152997284C20BAF76D091360A2052898D1C230E49C67AEDE5138BD3A469885BB |
| macos-linkx4c-libusb-1.0.29-unplug.log（启动失败） | 952FE292A85DB3575CF1434D99A1A85E0FEA9568AF6E4597D95B8CCCA048264C |

## 未覆盖与工程状态

实际进程争用输出 `occupied=NOT_TESTED (Windows-only fixture)`，不能当通过。只有一台 LinkX4C，未测多实体设备隔离、其它型号/boot/CDC、真正在途 OUT、持续吞吐、端点级取消影响范围及原生内存审计。本机为 arm64，不代表 macOS x64 已测。Linux ≥1.0.29 复测仍待完成。

本轮未刷固件、未复位设备、未修改 CAN/UART/电阻、电源策略或系统 libusb；未提交、合并、推送或发布。结束时没有活动探针。
