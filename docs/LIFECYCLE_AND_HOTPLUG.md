# 生命周期与热插拔行为（P0 开发版）

这是通用 USB fork 的行为说明，不包含设备协议。它保留上游程序集/命名空间，NuGet 身份改为 `BeanPaste.LibUsbDotNet`；不要在同一进程混用官方包和 fork。当前只完成本地工程验证，尚未发布，许可交付门槛见 [P0 基线](P0_BASELINE.md)。

## 传输与关闭

- 保留原有异步方法签名；端点 `ReadAsync/WriteAsync` 新增 `CancellationToken` 重载，`UsbDevice.ControlTransferAsync` 也有可取消重载。为避免强制第三方 `IUsbDevice` 实现者新增方法，后者仅加在具体 `UsbDevice` 类上。
- token 提前取消不分配 native transfer；在途取消只调用原生取消请求，不提前结束 Task，不提前释放 pin、发送临时缓冲区或设备句柄引用。
- 原生最终状态为 Cancelled 且 token 已取消时 Task 进入取消态；若完成先赢并返回 Completed，则保留成功及实际长度。未传 token 的旧接口仍把原生 Cancelled 映射为 `Error.Io`。取消不保证已发送数据撤回，也不保证尚无部分数据传输。
- 正常完成、提交失败、原生超时、断开、取消均按各自分支回收；成功提交后只有最终回调可以释放 native transfer。未知注册 ID 不向原生调用栈抛托管异常。
- 在途传输也保持所属 context 存活，避免事件线程故障后 context 提前终结；完成后不额外延长其生命周期。仍要求上层显式 Dispose，不把进程终结当正常关闭流程。
- `Close` 顺序为：禁止该打开句柄的新异步提交 → 请求取消该句柄全部在途传输 → 等待最终回调回收资源 → 关闭句柄 → 最后在无需热插拔、且无其它打开设备时停止事件线程。
- `Close` 最多等待 5 秒。超时抛 `TimeoutException`，保留句柄、在途资源和事件线程，不假装关闭成功；该句柄继续拒绝新异步传输。待最终回调完成后重试 `Close`。成功关闭后可以重新 `Open`，新打开句柄有独立生命周期。
- 成功 `Close` 同时清空旧句柄的 claim/alt 缓存；重开后需重新 claim 实际接口，不会因为旧缓存而跳过原生 claim。
- context 释放先逐设备排空，之后停止事件线程并释放 context。某设备排空超时则 context 不标为已释放，允许重试。关闭一个设备不取消另一个设备的传输。
- 枚举、克隆和热插拔返回的设备引用都保留原生 context；`UsbContext.Dispose` 后禁止业务操作，但仍可安全 Dispose 尚未释放的设备/枚举列表。最后一个设备引用归还之后才真正调用 `libusb_exit`。建议仍按设备/列表 → context 的顺序及时释放，不能依赖 GC 代替确定性清理。已释放设备或 context 上的 `Clone` 在进入原生调用前拒绝。
- 禁止在原生 USB 事件线程同步 `Close/Dispose`；在修改关闭状态之前抛 `InvalidOperationException`。高层业务应在自己的任务/调度线程执行关闭。
- 同步原生调用及直接操作公开 `DeviceHandle/NativeMethods` 不属于托管异步注册表；调用方仍须串行管理这些调用与关闭，不要绕过封装直接关闭在途句柄。
- 传输状态与设备到达/离线是独立观测。库保留原生 Stall→Pipe、NoDevice→NoDevice，不根据邻近离线事件改写完成错误。Windows 拔插现场已经观察到 Pipe 后收到 DeviceLeft；不能把 Pipe 本身当作离线证明，也不能因此跳过新到达和新句柄重连。

```csharp
using var stop = new CancellationTokenSource();
var read = reader.ReadAsync(buffer, timeout: 0, cancellationToken: stop.Token);
// ... 其它业务；buffer 在 read 完成之前不可读写复用
stop.Cancel();
try { await read; }
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
device.Close();
```

macOS 原生后端可能取消同端点的其它请求，不能承诺逐请求隔离取消；上层端点会话须处理其它请求也被取消的情况。本轮未验证 macOS 真机路径。

## 热插拔缓存与通知

1. 注册时检测 libusb 能力。支持则使用 native hotplug；不支持则每 250 ms 枚举比对。内部始终跟踪 arrival/left 两类变化，VID/PID/class 筛选在两种后端一致；用户事件标志只决定通知哪些事件。
2. 重复 arrival 只入缓存一次；未知 departure 忽略。注册失败回滚缓存和引用，不标记监控成功。注册返回前的同步 ENUMERATE 通知也可处理。
3. 原生回调更新缓存、唤醒等待并投递通知，不运行用户业务；用户 `DeviceEvent` 在独立串行任务队列执行，不是 UI 线程。上位机自行调度 UI 更新。
4. 普通通知队列容量为 256。慢消费者可能导致通知被拒绝，随后通过 `DeviceEventError` 报溢出；缓存和 DeviceManager 的状态信号不丢。收到溢出后刷新枚举，不把通知数量当完整硬件历史。
5. 单个事件订阅者异常会转入 `DeviceEventError`，不影响其它订阅者，也不越过 native 回调边界。错误处理器自身异常仅记 Trace。错误处理器同样不得长时间阻塞。
6. arrival 事件中的 `UsbDevice` 只在本次回调期间借用，不应由订阅者 Dispose；需要保留时调用 `Clone` 并由自己 Dispose 克隆。事件排队期间独立保留设备和 context 原生引用。
7. `UnregisterHotPlug` 使旧队列通知失效，但不强行中断已经执行的用户代码；不停止其它打开设备所需的 I/O 事件线程。通知内注销/释放 context 不等待通知任务自身。
8. 原生事件循环失败时停止、上报错误并唤醒设备等待。没有自动恢复承诺：处理错误后显式注销/重建监控和设备连接。不要把旧缓存当实时在线证明；真实 I/O 的 NoDevice 必须按离线处理。

## DeviceManager 等待契约

- `Start` 幂等；`Stop` 取消本等待器全部等待但不注销共享 context 监控；可再次 `Start`。只有创建时明确 `disposeContext=true`，等待器 Dispose 才释放 context。
- 同一个 finder 的多个等待各自独立，一个请求取消不取消其它请求；超时返回 null，不遗留注册污染下一次等待。
- `WaitForDeviceArrival` 可返回已有设备，但仍检查稳定连接时间；`WaitForNewDeviceArrival` 只接受调用后的新 arrival，重复 native 通知不算重连。
- 稳定连接时间包含在总超时内；候选设备消失即失效。支持 `Timeout.InfiniteTimeSpan` 和零总超时查询。自定义 finder 的 `Check(IUsbDevice)` 在 native 缓存锁外执行，不能保证慢 finder 不占用调用方任务。
- 返回值是调用方拥有的独立设备引用，即使监控注销也不会因为缓存引用释放而失效；调用方必须 Dispose。引用有效不代表物理设备还在线。
- context 注销/重建监控或 native 事件失败会使已有等待取消；必须显式重新发起等待。

## 一手依据

- libusb v1.0.29 [`io.c` — Cancellation / Memory caveats](https://github.com/libusb/libusb/blob/v1.0.29/libusb/io.c)：取消异步完成、最终回调前禁止释放、macOS 端点级取消限制。
- libusb v1.0.29 [`libusb.h` — Hotplug callbacks / ENUMERATE](https://github.com/libusb/libusb/blob/v1.0.29/libusb/libusb.h)：回调最小处理、重复 arrival 和无对应 arrival 的 departure。
- libusb [Polling and timing — interrupt_event_handler](https://libusb.sourceforge.io/api-1.0/group__libusb__poll.html)：唤醒专用事件线程。
- libusb [Library initialization/deinitialization — libusb_exit](https://libusb.sourceforge.io/api-1.0/group__libusb__lib.html)：反初始化之后不得再调用 libusb，必须先关闭所有设备。
- Microsoft [SafeHandle.DangerousAddRef — Remarks](https://learn.microsoft.com/dotnet/api/system.runtime.interopservices.safehandle.dangerousaddref)：每次成功的引用保留必须匹配 `DangerousRelease`，用来保护原生父对象的释放顺序。
- libusb v1.0.29 [`windows_common.c` — windows_handle_transfer_completion, L795–L843](https://github.com/libusb/libusb/blob/v1.0.29/libusb/os/windows_common.c#L795-L843)：Windows `ERROR_GEN_FAILURE` 映射为 `LIBUSB_TRANSFER_STALL`，`ERROR_FILE_NOT_FOUND/ERROR_DEVICE_NOT_CONNECTED/ERROR_NO_SUCH_DEVICE` 映射为 `LIBUSB_TRANSFER_NO_DEVICE`。这是原生映射依据，尚不能代替本次拔插的原生错误日志。

以上是实现依据，不代表本 fork 已经获得三平台硬件验证。实测范围见 [报告](../tests/P0_USB_LIFECYCLE_TEST_REPORT.md)。
