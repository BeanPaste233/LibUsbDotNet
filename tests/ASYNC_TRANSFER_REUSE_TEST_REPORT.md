# Async transfer 复用报告

2026-10-28；分支 `codex/perf-async-transfer-reuse`，保留此前全部未提交修改。本轮只改 `src/LibUsbDotNet/LibUsb/AsyncTransfer.cs` 及其 `AsyncTransferTests.cs`，没有引入 DM 协议、改 public Task API 或发布包。

成功请求的 native transfer + 完成状态对象按打开句柄复用，空闲缓存上限 16；API 实例/ISO 容量不匹配不借用。pin 和取消注册每笔独立清理，旧 Task/TCS 不重置；逐 I/O 的 Drained TCS 改为 Close 才创建的设备排空任务。仍有独立完成 TCS/Task，因此不是零分配实现。

结果：

- Debug/Release 各 78/78 通过，包括原有取消/Close 超时/多设备/context 生命周期与新增复用、旧 token/Task、池上限、API/ISO 隔离、GC 最终回收。
- 250 次同句柄成功请求只 Allocate 1 次，Close 时 Free 1 次。新增红灯在旧实现 Allocate 2 次失败，记录 `artifacts/test-results/transfer-reuse-red.trx`。
- `netstandard2.0/net8.0/net10.0` Release 均构建成功；有 NuGet NU1900 漏洞源缓存警告，未完成漏洞源审计。
- 新改 CS 文件格式检查通过；本次 AsyncTransfer diff 检查通过。全仓其它既有脏文件存在 CRLF/尾空白警告，未清理。
- Windows LinkX4C 消费端组合版三轮 FD64 短测、三轮 FD8 短测完整通过；FD64 分配约从 480 降至 392 B/帧，吞吐未明显变化。百万帧仍失败：RX/真 echo 各缺 88、设备消息块丢弃对应，主机通知零丢弃但 Malformed 增 1，不能认为复用解决了长期可靠性问题。

详细真机二进制哈希、各阶段失败与对照、命令和未覆盖项：`E:/CSharp_Projects/DM_DeviceSDK/tests/TX_PIPELINE_TRANSFER_REUSE_TEST_REPORT.md`。当前 net10.0 Release USB SHA256 `E96A2213DDDA6CFBF4AB0C12537B3CA2EA24DBDD34B9973F7C527F887E936486`，native libusb 为 1.0.30.12037，未替换系统库。未测本轮 x86/Linux/macOS、多设备真机和候选消费。

测试命令（本仓库根）：

```powershell
& 'C:/Program Files/dotnet/dotnet.exe' test src/LibUsbDotNet.Tests/LibUsbDotNet.Tests.csproj -c Release --no-restore --logger 'trx;LogFileName=NEW.trx' --results-directory artifacts/test-results --nologo --verbosity minimal -m:1 -nr:false
```

最终 TRX `transfer-reuse-final-debug.trx`、`transfer-reuse-final-release.trx`；两者均在 `artifacts/test-results/`。测试改为在 Close 后检查池释放，没有删除取消前不能释放、资源引用与计数断言。

依据：libusb v1.0.30 官方 `libusb/io.c`（allocation/fill/submit 分离便于复用、异步取消、事件处理上下文）：https://github.com/libusb/libusb/blob/v1.0.30/libusb/io.c ；官方异步 I/O 文档：https://libusb.sourceforge.io/api-1.0/group__libusb__asyncio.html 。Windows 真机不代表 macOS 同端点取消行为已验证。
