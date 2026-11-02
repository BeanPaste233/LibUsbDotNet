# Async transfer 复用用例

1. 同句柄成功完成后重复提交：native 分配复用，旧 Task 的结果不被重置，旧 token 不能取消新请求。
2. 每句柄最多缓存 16 个空闲项；不同句柄、native API、ISO 容量不可混用，超过池容量的完成项立即释放。
3. Submit 错误/异常、Allocate 返回空、Pin 抛错均不保留 pin、句柄引用、字典注册或原生存储；复用项提交失败同样释放。
4. 请求取消或 Close 后仍等最终回调：取消成功、NotFound、IO 错误/异常都不能提前 unpin/Free。Close 幂等、超时可重试，多设备互不影响。
5. 并发取消/完成多轮，无重复释放；同句柄循环复用不受前代取消影响。原生完成 Task 前必须已经 unpin。
6. Close 释放全部池存储；异常遗失空闲池不永久 root 设备，SafeHandle 能最终释放 native 存储。
7. Windows CAN 消费端对比旧/新 USB DLL，保持同 native DLL、固件和回环模式，完整性检查不放宽。
