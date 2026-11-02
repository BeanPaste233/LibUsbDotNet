# P0 基线与维护规则

日期：2026-10-19。本记录允许进入隔离的本地研发验证，不是对闭源产品分发的法律合规结论。

## 冻结基线

- fork：`https://github.com/BeanPaste233/LibUsbDotNet.git`。
- 上游：`https://github.com/LibUsbDotNet/LibUsbDotNet.git`。
- 基线：`e57f336baa1b36897767539878686954947dab95`；本轮 `git ls-remote` 确认上游 master 与 fork 的初始 HEAD 相同。
- 工作分支：`chore/p0-usb-baseline`；不直接修改 master，不提交/推送，除非用户另行要求。
- 独立 PackageId 已配置为 `BeanPaste.LibUsbDotNet`；保留上游程序集/命名空间和版本计算机制，不将本地修订冒充官方同版本包。未制作或发布正式 nupkg。
- 定位：独立通用 USB 库，不加入 DM 型号、协议、CAN、CRC 或串口业务；底层 libusb 保持动态加载。

## 许可核实与发布门槛

已直接读取本基线的根 `LICENSE`（LGPLv3）、项目 `PackageLicenseExpression=LGPL-3.0-or-later`，并扫描文件头。库源码中有 90 个文件保留 GPL 文件头，1 个文件使用 LGPL SPDX 标识，不能把它简化为单个文件问题。

补查到的上游一手依据：

- [Issue #4：Update license information to clarify we're on LGPL](https://github.com/LibUsbDotNet/LibUsbDotNet/issues/4)：明确说明项目按 LGPLv2+ 授权，原文件头需同步；该 issue 已关联迁移提交关闭。
- [Issue #130：LGPL license is confusing. Can someone help?](https://github.com/LibUsbDotNet/LibUsbDotNet/issues/130)：上游回应闭源应用的依赖使用场景，但不代替对本产品具体分发方式的审查。
- 提交 `873b0bf992e0a9a17d33ef12861006ebbc09a807` 添加根 LGPLv3；`26bd7d86061b4841cc8e9696f0b02426a27fcb66` 把项目旧 GPL 链接修正到根 LICENSE；当前项目使用 LGPL-3.0-or-later 声明。

这些记录补充了此前“只看到文件头不一致”的证据，因此可以按上游项目声明继续本地技术验证；本轮不修改任何原版权/许可头，也不宣称历史标注已被本 fork 修正或已获得额外授权。

正式对外发包或接入闭源产品前，必须核实具体基线/补丁的授权及交付要求，准备完整许可、源代码/构建材料及替换部署说明。动态链接本身不等于全部义务已满足；未完成前不发布可分发包。本轮不创建上游 issue、不联系第三方，也不替用户作法律合规承诺。

## 维护规则

1. 保留上游历史、版权、公共命名及已有通用能力；最小补丁、每个问题有回归测试。
2. 不重生成全部 P/Invoke、不额外套一套重复公共 USB API、不引入 DM 协议。
3. 本地诊断产物不是正式发布包；单独记录原生 libusb 来源/版本。
4. 构建成功和真机验证分别记录。三平台、六架构及硬件缺失项必须标“未测”。
5. 先验证失败清理、内存固定、事件线程停止与关闭，再进入通道协议和性能实现。

## 初始构建结果

命令（仓库根）：`dotnet build src/LibUsbDotNet/LibUsbDotNet.csproj -c Release --nologo -v minimal`。

- `netstandard2.0` / `net8.0` / `net10.0` 均编译通过，0 错误。
- 4 条 NU1900 警告：本机 NuGet 将 HTTP 缓存目录另配置为 local 源，漏洞数据缓存发生路径冲突。未清理/删除用户全局缓存，也未关闭漏洞检查；后续使用限定的官方源和独立缓存复核。
- 尚未运行本 fork 的回归测试或硬件测试；不能将初始构建记为 P0b/P0c 完成。

## 当前验收记录

上面的“尚未运行”仅描述初始冻结时刻。后续本地生命周期回归已完成，当前 73 项通过，Windows LinkX4C 的自动测试及 1.0.30 人工拔插已通过；完整范围见 [P0 测试报告](../tests/P0_USB_LIFECYCLE_TEST_REPORT.md)。Linux 在卸载 dmcan 后的新一轮拔插恢复枚举，系统 libusb 1.0.25 下自动真机测试、1000 次完整生命周期及一次人工 IN 在途拔插/重连通过；≥1.0.29 复测仍待完成，详见 [Linux 报告](../tests/P0_LINUX_USB_TEST_REPORT.md)。macOS arm64 已通过有线地址完成部署，实际 libusb 1.0.29.11953 下自动项目、1000 次完整生命周期及一次人工 IN 在途拔插/重连通过；private/handles 当前采样无有效数据，不宣称原生无泄漏，详见 [macOS 记录](../tests/P0_MACOS_USB_TEST_REPORT.md)。多实体设备、实际非 Windows 争用、其它型号/boot/CDC、OUT 在途与持续吞吐等缺项仍未验收；P0 整体未完成，许可分发门槛保持不变。
