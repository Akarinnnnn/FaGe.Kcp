# 主要API参考

## KcpConnection 主要方法
| 方法 | 参数 | 说明 | 重要行为细节 |
|------|------|------|-------------|
| `ReceiveOnceAsync(CancellationToken)` | `cancellationToken`: 取消令牌 | 从传输层接收一次数据 | 1. 异步阻塞操作，等待数据到达<br>2. 同一时间只能有一个进行中的调用<br>3. 使用"检查-等待-清空"模式避免数据包丢失 |
| `ManualInputOnce(UdpReceiveResult)` | `receiveResult`: UDP接收结果 | 手动输入外部接收的数据 | 1. 用于多路复用场景<br>2. 同步方法，立即返回<br>3. 检查并更新RemoteEndpoint |
| `UpdateAsync(uint timestamp, CancellationToken)` | `timestamp`: **当前UTC时间戳(毫秒)**<br>`cancellationToken`: 取消令牌 | 更新KCP内部状态 | 1. **必须定期调用**（推荐10ms间隔）<br>2. 时间戳必须使用UTC时间<br>3. 内部可能调用`FlushAsync()`发送数据 |
| `SendAsync(ReadOnlyMemory<byte>, CancellationToken)` | `buffer`: 要发送的数据<br>`cancellationToken`: 取消令牌 | 发送应用层数据，返回发送结果 | 1. 在流模式下可能返回`sentCount: 0`（数据合并到前一个包）<br>2. 立即返回，实际发送由后续`UpdateAsync()`触发<br>3. 返回的`Task`在数据放入发送队列时完成，不代表网络发送完成 |
| `ReceiveAsync(CancellationToken)` | `cancellationToken`: 取消令牌 | 接收应用层数据包，返回`KcpApplicationPacket` | 1. 返回的`KcpApplicationPacket`必须在处理完成后立即`Dispose()`<br>2. 空数据包(`IsEmpty: true`)不需要释放<br>3. 同一时间只能有一个等待中的调用 |
| `Dispose()` | 无 | 释放连接资源 | 1. 同步释放所有内部资源<br>2. 取消所有挂起的异步操作<br>3. 确保在所有操作完成后调用 |
| `ConfigureNoDelay(bool, int, int, bool)` | `nodelay`: 是否启用无延迟<br>`interval`: 更新间隔(ms)<br>`resend`: 快速重传阈值<br>`nc`: 是否关闭拥塞控制 | 配置协议参数 | 1. 线程安全，可在初始化时调用<br>2. 后续调用需要考虑同步<br>3. 推荐配置：`(true, 10, 2, false)`用于低延迟 |

## KcpConnection 主要属性
| 属性 | 类型 | 说明 | 线程安全 |
|------|------|------|----------|
| `ConnectionId` | `int` | 连接标识符 | ✅ |
| `RemoteEndpoint` | `IPEndPoint` | 远程端点 | ✅ |
| `IsStreamMode` | `bool` | 是否启用流模式 | ✅ |
| `MTU` | `uint` | 最大传输单元，**默认1400字节** | ✅ |
| `SendWindowSize` | `int` | 发送窗口大小 | ✅ |
| `RecvWindowSize` | `int` | 接收窗口大小 | ✅ |
| `PacketPendingSentCount` | `int` | 待发送数据包数量 | ❌ **必须在工作器线程中读取** |
| `IsWindowFull` | `bool` | 接收窗口是否已满 | ❌ **必须在工作器线程中读取** |

## KcpApplicationPacket 主要成员
| 成员 | 类型 | 说明 |
|------|------|------|
| `Result` | `ReadResult` | 核心读取结果，通过`Result.Buffer`获取数据 |
| `IsEmpty` | `bool` | 判断数据包是否为空 |
| `IsNotEmpty` | `bool` | 判断数据包是否非空 |
| `Dispose()` | `void` | 释放数据包资源，**必须调用** |

## KcpConnection 线程安全级别

| 方法 | 线程安全 | 并发使用限制 |
|------|----------|--------------|
| `SendAsync()` | ❌ 不安全 | 必须通过专用工作器或同步机制调用 |
| `ReceiveAsync()` | ❌ 不安全 | 同一时间只能有一个等待中的调用 |
| `UpdateAsync()` | ❌ 不安全 | 不能与其他方法并发调用 |
| `ReceiveOnceAsync()` | ❌ 不安全 | 同一时间只能有一个进行中的调用 |
| `ManualInputOnce()` | ❌ 不安全 | 不能与其他方法并发调用 |
| `Dispose()` | ⚠️ 有条件 | 在所有操作完成后调用，与其他操作并发可能导致异常 |
| `ConfigureNoDelay()` | ⚠️ 有条件 | 可在初始化时调用，后续调用需同步 |
---
本文完全由人工编写，没有机器人参与🤖
IP：生化斯坦