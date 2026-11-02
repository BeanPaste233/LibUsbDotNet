# P0 Linux USB 验收记录

日期：2026-10-20。对象：LibUsbDotNet fork `e57f336baa1b36897767539878686954947dab95` + `chore/p0-usb-baseline` 未提交 P0 改动，不是 C SDK 或 Rust 包的历史验收。

## 当前结论

**Linux x64 / LinkX4C 自动真机测试、1000 次完整生命周期及一次人工 IN 在途拔插/重连通过，实际加载系统 libusb `1.0.25.11696`。** libusb ≥1.0.29 复测及其它硬件/平台场景尚未完成，不宣称 P0 整体验收。

用户要求卸载 `dmcan` 后已于 11:30:52 执行 `rmmod dmcan`。新一轮人工拔插后 11:34:04 成功枚举，三个 vendor 接口均无内核驱动绑定，`dmcan` 未自动重载。这个先后关系不足以单独证明前两轮描述符失败的根因就是 `dmcan`。

## 环境与被测对象

| 项目 | 实际状态 |
| --- | --- |
| 主机 | `dm5080@172.18.20.212`，hostname `server` |
| OS / 架构 | Ubuntu 22.04.4 LTS，Linux `6.8.0-138-generic`，x86_64 |
| 探针 | `DM_DeviceSDK/proto/LibUsbP0` 与通用 `tools/UsbProbe`，自包含 .NET 8.0.31 |
| 原生库 | 实际日志 `native=1.0.25.11696`，没有替换系统 libusb |
| 设备 | `34B7:4543`，DM-LinkX4C，位置 `1-10`，Bus 001 Device 018，USB 480 Mbps |
| 固件实读 | `App/L4 V2.0.0 [c237c95 Oct 19 2026 12:07:12]` |
| 接口 | 0/1/2 均为 class `0xFF`；无 CDC；三个接口 claim 成功 |
| 权限 | USB 节点模式 `0666`，测试以普通 SSH 用户执行；未改 udev 或权限 |

## 设备恢复时间线

以下为主机当地时间 UTC+08:00：

| 时间 / 位置 | 结果 |
| --- | --- |
| 11:24:11–11:24:13，Hub `1-11.4` | 描述符 `-32`、地址设置 `-71`，最终无法枚举 |
| 11:28:14–11:28:16，直连 `1-10` | 描述符 `-71`，最终无法枚举 |
| 11:30:52，卸载驱动 | `rmmod dmcan` 成功，确认 `/sys/module/dmcan` 不存在 |
| 11:34:04，新一轮拔插 | 成功枚举 LinkX4C；`dmcan` 未自动重载，vendor 接口无绑定驱动 |

证据：`artifacts/test-results/p0-linux-preflight-20261020.log`、`p0-linux-dmcan-unload-20261020.log`、`p0-linux-after-dmcan-unload-replug-20261020.log`。

## 部署与复现

两个 linux-x64 自包含探针已成功部署到隔离目录 `/home/dm5080/libusbdotnet-p0-20261020/{LibUsbP0,UsbProbe}/`，没有覆盖旧 C#/C SDK 台架目录。

- 归档 `artifacts/linux-p0/probes-linux-x64.tgz`：64322020 B，SHA256 `D722B9E7E3DB4C249B47BA33C140397D41F9E4D7407570615C8AE87A568C5CAF`，远端核对一致。
- 两套 `LibUsbDotNet.dll` SHA256：`0887A42A34553A186D2E1E669EFE78143E8B5982692F0BC08306E85566B524C5`。
- SCP 最初失败；显式指定 `-S C:/Windows/System32/OpenSSH/ssh.exe -O` 后上传成功，不能把最初失败直接归因文件保护。

实际运行：

```bash
cd /home/dm5080/libusbdotnet-p0-20261020/UsbProbe
./UsbProbe --vid 0xFFFF --pid 0xFFFF --cycles 1000
cd /home/dm5080/libusbdotnet-p0-20261020/LibUsbP0
./LibUsbP0 --pid 0x4543 --cycles 1000
```

## 系统 libusb 1.0.25 实测

| 项目 | 结果 |
| --- | --- |
| 无匹配设备枚举 / 释放 1000 次 | `DONE errors=0` |
| 三接口 claim 与版本读取 | 通过；42/512 B 短包，CRC 正确，版本/构建如上 |
| 50 ms 空闲读取超时 | 通过，51.40 ms |
| token 在途取消 | 通过，1.04 ms |
| 4 个在途 IN 中 Close | 通过，6.90 ms |
| 同对象重开后查询 | 通过 |
| context 在途 Dispose | 通过，2.78 ms |
| 初始热插拔缓存 / 多等待独立取消 / 注销后引用可用 | 通过，arrival=1、errors=0 |
| 完整生命周期 1000 次 | 通过，5286.06 ms；包含枚举、打开、claim、版本查询、release、关闭、释放 |
| 子进程占用 | **未测**，当前 fixture 仅实现 Windows，日志明确为 `NOT_TESTED` |
| 人工在途拔插 / 新引用重连 | 一次完整通过，退出 0；arrival=2、left=1、errors=0；在途原生结果 `Io / 0 B` |

1000 次强制 GC 后资源点：managed 168200→205608 B，private 129581056→152547328 B，handles 64→66，threads 14→14；后半程 private 基本平台。未崩溃，不据此宣称所有原生分配均无泄漏。

原始版本命令（本轮唯一设备命令，不发送 CAN/UART 数据）：

```text
OUT A5060000E0015A
IN  A506002200414C3402000063323337633935204F637420313920323032362031323A30373A3132B6085A
```

本机日志：`artifacts/test-results/p0-linux-no-match-system-libusb.log`、`p0-linux-linkx4c-system-libusb.log`；远端为隔离目录下 `linux-no-match-system-libusb.log`、`linux-linkx4c-system-libusb.log`。

## 人工 IN 在途拔插与恢复

第一轮探针 11:46:33 启动，11:56:33 达到 10 分钟等待上限退出；内核记录的用户操作在 11:59:08 拔出、11:59:13 接回，晚于探针退出。该轮不计设备/库拔插失败，也不计通过；保留日志 `p0-linux-linkx4c-system-libusb-unplug-timeout.log`。

第二轮使用同一探针与系统原生库，12:00:24 启动并确认 `READY_UNPLUG`，用户再次拔插，完整退出 0。日志时间换算为 UTC+08:00：

| 时间 | 观察结果 |
| --- | --- |
| 12:00:54.401 | 在途读完成 `(Io, 0)`，原生日志 URB status=-71、low-level bus error；未观察到在途 `NoDevice` |
| 12:00:54.404 | 收到 `DeviceLeftEventArgs` |
| 12:00:54.411 | 旧句柄发送版本请求被拒，错误 `NoDevice` |
| 12:00:54.412 | 旧句柄正常关闭，进入新引用等待 |
| 12:01:00.322 | 收到新 `DeviceArrivedEventArgs` |
| 12:01:00.827 | 等待器返回位置 `1-10` 的新引用，符合设定 500 ms 稳定窗口 |
| 随后 | 重新 claim 0/1/2；版本/构建与初始相同，42/512 B、CRC 正确 |

最终 `unplug-reconnect=PASS arrivals=2 departures=1 errors=0; in-flight-native-error=Io`。这些时间是软件日志事件，不是物理拔线到通知的精确延迟。完整退出无 `still referenced` 警告；不等同原生内存审计通过，也不代表 OUT 在途或反复拔插已经覆盖。

重连后为 Bus 001 Device 020，`dmcan` 未自动重载，探针进程已结束。证据：本机 `artifacts/test-results/p0-linux-linkx4c-system-libusb-unplug-retry1.log`，远端 `linux-linkx4c-system-libusb-unplug-retry1.log`；执行命令仍为 `./LibUsbP0 --pid 0x4543 --unplug`。

## 新版原生库准备与限制

≥1.0.29 尚未部署，不能把 1.0.25 结果算成新版结果。没有全局安装或替换系统库。

1. 官方 1.0.30 release tar.bz2 下载文件不是有效 bzip2，头部为 `%TSD-Header-###%`，未解压或绕过保护。
2. 正常 Git 通道克隆官方 `v1.0.30` 成功，提交 `87a55632db62c9bdc58cd31d3ccfa673f1bb017f`；`bootstrap.sh` 生成的 configure 不完整，配置失败。
3. 独立克隆官方 `v1.0.29`（`15a7ebb4d426c5ce196684347d2b7cafad862626`），bootstrap 在 m4 读取 `libusb/version.h` 时失败；直接工具读取该头文件出现保护格式，属于工具/文件读取环境异常，尚未确认完整根因。没有修改第三方源码或安全配置。

远端日志：`native-bootstrap.log`、`native-bootstrap-c-locale.log`、`native-configure.log`、`native-1.0.29-bootstrap.log`。不把该构建失败归因 LibUsbDotNet。

## 未覆盖与边界

- 新版动态 libusb 下的复测、实际进程占用与多个实体设备隔离。
- 其它型号、boot/CDC、OUT 在途取消/拔插、持续吞吐、CAN 高流量与长期原生分配审计。
- macOS 与其它 RID 真机；当前只有一台 Linux LinkX4C，不能从 Windows 或 C/Rust 历史结果迁移结论。

未刷固件、未主动复位 USB、未改 CAN/UART/电阻参数，未改自动加载或黑名单、未卸载其它驱动。未提交、合并、推送或发布。
