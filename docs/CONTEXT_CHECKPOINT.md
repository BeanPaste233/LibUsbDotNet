# 当前工作检查点（2026-11-02，bean_dev 提交与推送）

## 1. 当前目标与断点

用户要求将 USB 库推送到远端，不创建 PR。此前已按要求新建 bean_dev，并删除本地 chore/p0-usb-baseline、codex/perf-async-transfer-reuse；旧分支没有独有提交，全部工作区改动均已保留。
本检查点记录提交前状态：Debug/Release 各 78/78 测试、Release 三目标构建已通过。下一步第一个具体动作：显式暂存本库的 34 个变更文件，检查暂存内容后提交到 bean_dev，再推送 origin/bean_dev；不修改 master，不打 tag，不创建 PR。

## 2. 待办顺序

1. 检查拟提交文件范围，提交标题使用 feat(usb): improve lifecycle, hotplug and reusable async transfers；提交身份使用用户指定的豆摸鲨 <beanpaste233@qq.com>，仅对该命令生效。
2. 执行 git push -u origin bean_dev，不强推、不推 master、不创建 PR。
3. 核对 git rev-parse HEAD 与 git ls-remote --heads origin bean_dev 一致，工作树干净且本地只保留 bean_dev/master。
4. 本轮不重出 SDK 包、不操作其他仓库、不刷固件。长期丢帧问题仍按正式报告记录，不因推送源码而宣称验收通过。

## 3. 环境与工程状态

- 工作目录 E:/CSharp_Projects/LibUsbDotNet；当前 bean_dev 与 master 在提交前均指向 e57f336baa1b36897767539878686954947dab95。无 stash，只有当前一个 worktree。
- 提交前 13 个已跟踪文件修改、21 个新增文件；包括生命周期/热插拔、transfer 复用、测试、探针及文档。未加入原生二进制或构建缓存。
- origin 为 https://github.com/BeanPaste233/LibUsbDotNet.git；提交前远端仅有 master，bean_dev 尚未创建。认证仅使用已有 Git helper，不读取、输出或写入凭据。
- 工具 C:/Program Files/dotnet/dotnet.exe；读 rg、写 apply_patch。不开前台 IDE，不新建/恢复子代理；本轮无需硬件、未刷写或复位任何固件。

## 4. 已执行命令和证据

- 以下命令工作目录均为 E:/CSharp_Projects/LibUsbDotNet。
- Release：& 'C:/Program Files/dotnet/dotnet.exe' test src/LibUsbDotNet.Tests/LibUsbDotNet.Tests.csproj -c Release --logger 'trx;LogFileName=bean-dev-prepush-release-20261102.trx' --results-directory artifacts/test-results --nologo --verbosity minimal -m:1 -nr:false。78/78，通过，0 跳过。
- Debug：& 'C:/Program Files/dotnet/dotnet.exe' test src/LibUsbDotNet.Tests/LibUsbDotNet.Tests.csproj -c Debug --no-restore --logger 'trx;LogFileName=bean-dev-prepush-debug-20261102.trx' --results-directory artifacts/test-results --nologo --verbosity minimal -m:1 -nr:false。78/78，通过，0 跳过。
- 构建：& 'C:/Program Files/dotnet/dotnet.exe' build src/LibUsbDotNet/LibUsbDotNet.csproj -c Release --no-restore --nologo --verbosity minimal -m:1 -nr:false。netstandard2.0/net8.0/net10.0 成功，0 错误；NU1900 漏洞数据缓存警告保留，不能宣称依赖漏洞审计完成。
- git -c core.whitespace=cr-at-eol diff --check 通过；默认检查报告的是 CRLF 行尾，未批量格式化源码。
- 拟提交目录常见凭据模式扫描未匹配；构建产物和 TRX 保留在被忽略的 artifacts/、bin/、obj/ 中。
- 历史复用验证与硬件边界详见 tests/ASYNC_TRANSFER_REUSE_TEST_REPORT.md；SDK 组合报告见 E:/CSharp_Projects/DM_DeviceSDK/tests/SEQUENTIAL_CAN_OPTIMIZATION_TEST_REPORT.md。本轮没有重跑硬件测试。

## 5. 已验证/否决

- 成功完成后复用native+完成状态对象，最多16空闲/句柄；按nativeApi和ISO容量匹配，不限制active并发。失败/取消直接释放，Close释放池并等最终callback。
- 每笔独立完成Task/TCS不重置，旧Task可重复/晚await；取消注册退出、unpin/句柄引用归还后才归池。去掉每IO Drained TCS，Close按设备懒建排空TCS。
- 250次同句柄成功请求native Allocate1/Close Free1；旧取消、并发取消/完成、池上限/API/ISO/设备隔离、Close超时重试、GC遗失池兜底通过。
- 组合FD64短测三轮约2.75万fps、分配480→392B/帧；FD8无稳定吞吐收益。百万帧RX/真echo各缺88，设备消息块丢弃对应，主机通知0丢弃但Malformed1；不归责单方，不按通过记。
- libusb官方v1.0.30 io.c及asyncio文档允许完成后复用；取消是异步的，最终callback之前不能释放资源。不得在事件callback阻塞等事件。
- 后续SDK P1/P3短测托管分配396.79→342.61B/帧、吞吐仍约2.75万fps；最新无profiler四路百万RX/真echo每路各缺15，设备消息块计数逐项对应。主机通知丢弃/解析/回调异常0，清理成功；长测仍未收口，不归责纯SDK或纯固件。旧8RX无稳定收益，不恢复。

## 6. 验收判据

本轮完成判据：bean_dev 的提交包含已核对的 USB 库改动，origin/bean_dev 与本地 HEAD 相同，工作树干净，master 保持不变，没有创建 PR 或 tag。源码推送不等于 NuGet 发布；离线测试不能替代跨平台真机或长期可靠性验收。
