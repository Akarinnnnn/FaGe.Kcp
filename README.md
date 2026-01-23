# FaGe.Kcp

FaGe.Kcp 是一个基于 KCP 协议实现库，专注于简化 KCP 协议在 .NET 环境下的使用，提供原生的连接管理、数据包收发、资源自动释放等核心能力。此外，还提供了原生异步收发API(async-await模式)。

## 时间戳使用说明

FaGe.Kcp中的时间戳使用分为两种场景：

### 1. 网络协议时间戳（必须两端一致）
KCP协议使用时间戳计算RTT（往返时间）和重传超时。**两端必须使用相同的时间基准**，推荐使用UTC时间确保跨机器一致性。

```csharp
// ✅ 推荐：使用UTC时间戳（两端都使用，确保一致性）
uint protocolTimestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
await connection.UpdateAsync(protocolTimestamp, cancellationToken);
```

### 2. 本机性能跟踪（仅本地有效）
```csharp
// 本机性能监控（不参与网络通信）
long localTimestamp = Environment.TickCount64;
LogPerformance(localTimestamp);
```

**关键原则**：只要两端使用相同的时间源，KCP就能正常工作。UTC时间是推荐的跨机器方案。

## 目录
- [快速开始](#快速开始)
  - [最小示例](#最小示例)
  - [并行架构说明](#并行架构说明)
- [核心概念](#核心概念)
  - [KcpApplicationPacket](#kcpapplicationpacket)
  - [KcpSendResult](#kcpsendresult)
  - [KcpConnection](#kcpconnection)
  - [流模式 vs 包模式](#流模式-vs-包模式)
- [实现细节](#实现细节)
  - [架构分层](#架构分层)
  - [关键设计决策](#关键设计决策)
  - [配置选项](#配置选项)
- [并发安全性](#并发安全性)
  - [线程安全级别](#线程安全级别)
  - [推荐的使用模式](#推荐的使用模式)
  - [ReceiveOnceAsync() 的重要特性](#receiveonceasync-的重要特性)
  - [已知的竞争条件](#已知的竞争条件)
  - [异常处理原则](#异常处理原则)
  - [性能与安全权衡](#性能与安全权衡)
  - [最佳实践总结](#最佳实践总结)
- [故障排除](#故障排除)
  - [并发相关问题](#并发相关问题)
  - [性能相关问题](#性能相关问题)
  - [网络相关问题](#网络相关问题)
  - [调试建议](#调试建议)
  - [常见错误代码](#常见错误代码)
  - [预防措施](#预防措施)
- [服务端使用提示](#服务端使用提示)
  - [连接管理建议](#连接管理建议)
  - [服务端架构选项](#服务端架构选项)
  - [注意事项](#注意事项)
- [进阶用法](#进阶用法)
  - [自定义传输层实现](#自定义传输层实现)
  - [WebSocket传输层](#websocket传输层)
  - [内存传输层（测试用）](#内存传输层测试用)
- [从其他KCP实现迁移](#从其他kcp实现迁移)
  - [主要区别](#主要区别)
  - [常见迁移问题](#常见迁移问题)
- [限制和注意事项](#限制和注意事项)
  - [已知限制](#已知限制)
  - [平台特定行为](#平台特定行为)
  - [性能特性](#性能特性)
- [资源释放](#资源释放)
- [主要API参考](#主要api参考)
  - [KcpConnection 主要方法](#kcpconnection-主要方法)
  - [KcpConnection 主要属性](#kcpconnection-主要属性)
  - [KcpApplicationPacket 主要成员](#kcpapplicationpacket-主要成员)
  - [KcpConnection 线程安全级别](#kcpconnection-线程安全级别)
- [许可证](#许可证)

## 快速开始

### 最小示例
```csharp
using FaGe.Kcp;
using FaGe.Kcp.Connections;
using System.Net;
using System.Net.Sockets;

async Task RunKcpExampleAsync(CancellationToken ct)
{
    var udpClient = new UdpClient();
    var server = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 8888);
    
    // 自定义握手（可参考neuecc/kcp实现）
    uint conv = await HandshakeAsync(udpClient, server, ct);
    
    var connection = new KcpConnection(udpClient, conv, server);
    
    // 使用Channel进行线程安全的发送。此处为简化示例。实践上可考虑传入Func<KcpConnection, ValueTask>
    var sendChannel = System.Threading.Channels.Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
    
    // 启动两个并行的任务
    var workerTask = KcpWorkerAsync(connection, sendChannel, ct);
    var sendProducerTask = SendProducerAsync(sendChannel, ct);
    
    // 等待任务完成
    await Task.WhenAll(workerTask, sendProducerTask);
}

// KCP工作器：处理所有KCP相关操作（串行执行）
async Task KcpWorkerAsync(
    KcpConnection connection,
    System.Threading.Channels.Channel<ReadOnlyMemory<byte>> sendChannel,
    CancellationToken ct)
{
    Task? udpReceiveTask = null;
    
    while (!ct.IsCancellationRequested)
    {
        try
        {
            // --- 阶段1：UDP输入处理 ---
            if (udpReceiveTask?.IsCompleted ?? false)
            {
                await udpReceiveTask.ConfigureAwait(false); // 处理到达的数据
                udpReceiveTask = null;
            }
            
            if (udpReceiveTask == null)
            {
                // 启动新的非阻塞接收
                udpReceiveTask = connection.ReceiveOnceAsync(ct);
            }
            
            // --- 阶段2：KCP状态更新 ---
            uint timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await connection.UpdateAsync(timestamp, ct).ConfigureAwait(false);
            
            // --- 阶段3：发送队列处理 ---
            while (sendChannel.Reader.TryRead(out var data))
            {
                try
                {
                    await connection.SendAsync(data, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 发送失败时记录日志，终止连接是可接受的
                    LogError($"发送失败，终止KCP连接: {ex.Message}");
                    throw; // 让异常流出工作器
                }
            }
            
            // --- 阶段4：应用数据接收 ---
            var packet = connection.TryReadPacket(ct);
                
            if (packet.IsNotEmpty)
            {
                ProcessData(packet.Result.Buffer);
                packet.Dispose();
            }
            
            // --- 阶段5：循环控制 ---
            // 根据是否有工作来动态调整等待时间
            bool hadWork = sendChannel.Reader.Count > 0;
            await Task.Delay(hadWork ? 1 : 10, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 正常退出
            break;
        }
        catch (Exception ex)
        {
            // 记录错误，但继续运行（除非是致命错误）
            LogError($"KCP工作器错误: {ex.Message}");
            await Task.Delay(100, ct).ConfigureAwait(false); // 错误退避
        }
    }
    
    // 清理：取消任何挂起的接收
    if (udpReceiveTask != null && !udpReceiveTask.IsCompleted)
    {
        try
        {
            await udpReceiveTask.WaitAsync(TimeSpan.FromMilliseconds(50), ct);
        }
        catch
        {
            // 清理期间的异常可忽略
        }
    }
}

// 发送生产者：用户通过此方法发送数据
async Task SendProducerAsync(
    System.Threading.Channels.Channel<ReadOnlyMemory<byte>> sendChannel,
    CancellationToken ct)
{
    int count = 0;
    while (!ct.IsCancellationRequested)
    {
        try
        {
            // 示例：每秒发送一条消息
            var message = $"Hello KCP {count++}";
            ReadOnlyMemory<byte> data = Encoding.UTF8.GetBytes(message);
            
            await sendChannel.Writer.WriteAsync(data, ct).ConfigureAwait(false);
            LogInfo($"已排队发送: {message}");
            
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            break;
        }
        catch (Exception ex)
        {
            LogError($"发送生产者错误: {ex.Message}");
            await Task.Delay(500, ct).ConfigureAwait(false); // 错误退避
        }
    }
}

// 使用示例
async Task Main()
{
    var cts = new CancellationTokenSource();
    
    Console.CancelKeyPress += (s, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
        Console.WriteLine("正在停止...");
    };
    
    try
    {
        await RunKcpExampleAsync(cts.Token);
        Console.WriteLine("正常退出");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("用户取消");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"错误退出: {ex.Message}");
    }
}
```

### 设计说明

1. **单工作器线程**：所有KCP操作在`KcpWorkerAsync`中顺序执行，避免并发问题
2. **Channel线程安全**：发送通过Channel实现多生产者到单消费者的转换
3. **非阻塞设计**：
   - UDP接收使用"检查-等待-清空"模式
   - 应用接收使用`TryReadPacket(CancellationToken)`非阻塞检查
4. **错误恢复**：非致命错误记录后继续运行
5. **优雅停止**：支持Ctrl+C和程序化取消

### 关键优势
 
✅ **无死锁风险**：不持有锁等待异步操作  
✅ **线程安全发送**：通过Channel支持多线程发送  
✅ **简单易懂**：代码结构清晰，易于理解和调试  

### 并行架构

```
┌─────────────────┐      ┌─────────────────┐
│ 发送生产者线程  │─────▶│   Channel队列    │
│  (多线程安全)   │      │   (线程安全)     │
└─────────────────┘      └────────┬────────┘
                                   │
                         ┌─────────▼─────────┐
                         │  KCP工作器线程     │
                         │ (单线程顺序执行)   │
                         │ 1. UDP输入        │
                         │ 2. KCP更新        │
                         │ 3. 发送处理       │
                         │ 4. 应用接收       │
                         └───────────────────┘
```

## 核心概念

### KcpApplicationPacket
KCP应用层数据包载体，**必须在使用后立即释放**：

```csharp
// ✅ 最优方案
KcpApplicationPacket packet = await connection.ReceiveAsync(ct);
// 或 packet = connection.TryReadPacket(ct);
if (packet.IsNotEmpty)
{
    ReadOnlySequence<byte> buffer = packet.Result.Buffer; // 直接使用，无需拷贝
    ProcessData(buffer);
    packet.Dispose(); // 立即释放
}

// ✅ 需要持久化时
KcpApplicationPacket packet = await connection.ReceiveAsync(ct);
if (packet.IsNotEmpty)
{
    byte[] dataCopy = packet.Result.Buffer.ToArray(); // 创建副本
    packet.Dispose(); // 立即释放
    // 可以安全地在任何地方使用dataCopy
}

// ❌ 错误：持有多个引用
var packet1 = await connection.ReceiveAsync(ct);
var packet2 = packet1; // 浅拷贝，共享缓冲区
// 后续分别释放会导致多重释放问题，例如同一线程运行以下代码：
packet1.Dispose(); // 正确释放
packet2.Dispose(); // 释放了后面数据包的分段
```

### KcpSendResult
发送操作结果封装，包含发送状态、字节数和失败原因。

### KcpConnection
核心连接类，管理UDP传输和KCP协议状态。

### 流模式 vs 包模式
| 特性 | 包模式 (默认) | 流模式 |
|------|---------------|--------|
| 消息边界 | 保持完整边界 | 可能拆包/粘包 |
| 适用场景 | 消息通信、RPC | 文件传输、流媒体 |
| 配置 | `IsStreamMode = false` | `IsStreamMode = true` |
| 分片处理 | 自动分片/重组 | 连续数据流 |

```csharp
connection.IsStreamMode = true; // 启用流模式
```

## 实现细节

### 架构分层
1. **应用层** - 简单安全的API (`KcpApplicationPacket`, `KcpSendResult`)
2. **协议层** - 完整KCP协议实现 (`KcpConnectionBase`)
3. **传输层** - UDP具体实现 (`KcpConnection`)

### 关键设计决策
- **字节序透明处理**：自动处理网络小端字节序与主机字节序转换
- **接收方驱动流量控制**：窗口满时不发送ACK，触发对端重传
- **异步友好**：原生支持的异步API，支持CancellationToken
- **资源自动管理**：内部缓冲区使用ArrayPool，减少GC压力

### 配置选项
以下配置选项数据源自原始实现作者skywind3000。
```csharp
// 低延迟配置（游戏、实时音视频）
connection.ConfigureNoDelay(
    nodelay: true,     // 启用无延迟模式
    interval: 10,      // 10ms更新间隔
    resend: 2,         // 快速重传触发次数
    nc: false          // 保持拥塞控制
);

// 高吞吐量配置（文件传输）
connection.ConfigureNoDelay(
    nodelay: false,    // 关闭无延迟模式
    interval: 50,      // 50ms更新间隔
    resend: 0,         // 禁用快速重传
    nc: true           // 关闭拥塞控制（仅在可控网络中使用）
);
```

## 并发安全性

### 线程安全级别

**重要**：FaGe.Kcp 的并发安全性有限，大多数核心方法**不是线程安全的**。这是为了性能和实现简易做出的设计权衡。

| 方法 | 并发安全性 | 风险说明 |
|------|-----------|----------|
| `SendAsync()` | ❌ 不安全 | 通过`QueueToSenderWithAsyncSource()`操作非线程安全的`snd_queue` |
| `ReceiveAsync()` | ❌ 不安全 | 底层`TryReadPacket()`操作非线程安全的`rcv_queue`，且`pendingReceiveTaskSource`存在竞争条件 |
| `UpdateAsync()` | ❌ 不安全 | 无任何同步措施，会修改内部连接状态（snd_queue、rcv_queue等） |
| `FlushAsync()` | ❌ 不安全 | 修改内部缓冲区状态 |
| `InputFromUnderlyingTransport()` | ❌ 不安全 | 修改接收队列和缓冲区 |
| `ReceiveOnceAsync()` | ❌ 不安全 | 调用`InputFromUnderlyingTransport()`，同样修改内部状态 |

### 有限支持的模式
- **`TryReadPacket()`**：有限支持多消费者，但必须保证顺序：`A读取包1 → A释放 → B读取包2 → B释放`
- **内部集合**：所有未使用`Concurrent`前缀的集合（`Queue<T>`、`LinkedList<T>`）均无同步机制

### 推荐的使用模式

#### ✅ 正确模式：专用异步工作器（推荐）
```csharp
public async Task KcpWorkerAsync(
    KcpConnection connection,
    Channel<byte[]> sendChannel,
    CancellationToken ct)
{
    Task? udpReceiveTask = null;
    
    while (!ct.IsCancellationRequested)
    {
        try
        {
            // --- 阶段1：UDP输入处理 ---
            if (udpReceiveTask?.IsCompleted ?? false)
            {
                await udpReceiveTask.ConfigureAwait(false); // 处理到达的数据
                udpReceiveTask = null;
            }
            
            if (udpReceiveTask == null)
            {
                // 启动新的非阻塞接收
                udpReceiveTask = connection.ReceiveOnceAsync(ct);
            }
            
            // --- 阶段2：KCP状态更新 ---
            uint timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await connection.UpdateAsync(timestamp, ct).ConfigureAwait(false);
            
            // --- 阶段3：发送队列处理 ---
            while (sendChannel.Reader.TryRead(out var data))
            {
                await connection.SendAsync(data, ct).ConfigureAwait(false);
            }
            
            // --- 阶段4：应用数据接收 ---
            var packet = connection.TryReadPacket(ct);
            
            if (packet.IsNotEmpty)
            {
                ProcessData(packet.Result.Buffer);
                packet.Dispose();
            }
            
            // --- 阶段5：循环控制 ---
            // 根据是否有工作来动态调整等待时间
            bool hadWork = sendChannel.Reader.Count > 0;
            await Task.Delay(hadWork ? 1 : 10, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 正常退出
            break;
        }
        catch (Exception ex)
        {
            // 记录错误，但继续运行（除非是致命错误）
            LogError($"KCP工作器错误: {ex.Message}");
            await Task.Delay(100, ct).ConfigureAwait(false); // 错误退避
        }
    }
}
```

### ReceiveOnceAsync() 的重要特性

#### 1. 非幂等性
每个`ReceiveOnceAsync()`调用处理一个数据包，多次调用会处理不同的数据包。

#### 2. 异步阻塞性
`ReceiveOnceAsync()`会等待直到有数据到达或取消，是异步阻塞操作。

#### 3. 顺序敏感性
必须按顺序处理`ReceiveOnceAsync()`调用结果，避免多个未完成的调用导致数据包处理顺序错误。

### 已知的竞争条件

#### 1. pendingReceiveTaskSource 竞争
```csharp
// 在 ReceiveAsyncBase 中：
pendingReceiveTaskSource = signalSource;

// 如果两个线程同时调用 ReceiveAsync()：
// 线程A: 设置 pendingReceiveTaskSource = sourceA
// 线程B: 设置 pendingReceiveTaskSource = sourceB （覆盖A）
// 结果：线程A的 TaskCompletionSource 可能永远无法完成
```

#### 2. 集合操作竞争
```csharp
// 同时调用 SendAsync() 和 UpdateAsync()：
// SendAsync: snd_queue.Enqueue(packet)
// UpdateAsync: snd_queue.TryDequeue(out packet) // 可能同时发生，导致异常
```

#### 3. ReceiveOnceAsync 并发调用竞争
```csharp
// 同时启动多个 ReceiveOnceAsync() 调用：
// 调用1: 开始接收数据包A
// 调用2: 开始接收数据包B
// 问题：两个调用可能并发修改内部状态，且数据包处理顺序不确定
```

#### 4. Dispose与其他操作的竞争
```csharp
// 线程A: await connection.SendAsync(data, ct);
// 线程B: connection.Dispose();  // 可能立即触发ObjectDisposedException

// 解决方案：使用CancellationToken协调
// 所有操作都检查同一个CancellationToken（或至少链接至这个Token）
// Dispose前取消该token
```

### 异常处理原则

#### 策略总结

| 异常类型 | 处理方式 | 理由 |
|---------|---------|------|
| `OperationCanceledException` (已知来源ct触发) | 捕获并`break` | 正常退出流程 |
| `SocketException` (超时/连接重置/网络变化等临时错误) | 捕获并继续 | 网络临时问题，可恢复 |
| `ObjectDisposedException` (取消时) | 捕获并`break` | 资源清理期间的预期异常 |
| **其他所有异常** | **不捕获，传播出去** | **让调用者感知严重错误** |

#### 为什么让异常流出工作器？

1. **调用者知情权**：调用者需要知道工作器是否因错误退出
2. **监控和告警**：外部系统可以监控工作器故障
3. **资源管理**：如果工作器因异常退出，可能需要重建连接
4. **调试信息**：完整的异常堆栈有助于问题诊断

### 性能与安全权衡

FaGe.Kcp 设计时的权衡：

1. **性能优先**：使用标准集合而非并发集合，减少锁开销
2. **简单性**：避免复杂的同步逻辑，简化代码结构
3. **明确责任**：将并发安全的责任交给使用者，提供灵活性。但代价是提高了编写门槛
4. **异步友好**：移除后台循环，完全拥抱async/await模式

### 最佳实践总结

1. **每个连接使用专用异步工作器**（如`KcpWorkerAsync`模式）
2. **使用"检查-等待-清空"模式管理`ReceiveOnceAsync()`调用**
3. **避免任何形式的并发方法调用**同一个连接实例
4. **让严重异常流出工作器**，使调用者能够感知和处理
5. **谨慎使用`ConfigureAwait(false)`**：
   - ✅ **推荐在**：控制台应用、ASP.NET Core应用、后台服务、库代码中使用
   - ⚠️ **避免在**：ASP.NET（而不是Core）、WPF/WinForms UI线程等需要同步上下文的环境中盲目使用
   - **原则**：如果代码需要回到原始上下文（如更新UI、操作ASP.NET HttpContext），不要使用`ConfigureAwait(false)`
6. **正确处理异步异常**：
   ```csharp
   // ✅ 正确：保留原始异常堆栈
   try
   {
       await workerTask; // 异步等待获得原始异常
       // workerTask.GetAwaiter().GetResult(); // 同步处理Task的推荐方式，可抛出原始异常
       // ProcessAggreatedException(workerTask.Exception); // 检视AggreatedException，适合需要AggreatedException的场合
   }
   catch (Exception ex)
   {
       // ex包含完整的异步调用堆栈
   }
   ```
7. **服务端使用连接池**，每个连接有独立的异步工作器
8. **通过Task状态监控工作器健康**（`IsFaulted`、`Status`属性）

## 故障排除

### 并发相关问题

#### Q1: 多个线程调用SendAsync()导致数据混乱或崩溃
**现象**：数据包丢失、顺序错乱、偶尔抛出`InvalidOperationException`

**原因**：`snd_queue`是非线程安全的`Queue<PacketBuffer>`，多线程并发访问导致竞争条件

**解决方案**：
1. **使用专用工作器模式**：所有发送操作通过单一工作线程进行
2. **使用Channel实现线程安全队列**：
   ```csharp
   private readonly Channel<ReadOnlyMemory<byte>> _sendChannel = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
   
   // 多线程安全发送
   public ValueTask SendThreadSafeAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
       => _sendChannel.Writer.WriteAsync(data, ct);
   
   // 在工作器中处理发送
   while (_sendChannel.Reader.TryRead(out var data))
   {
       await _connection.SendAsync(data, ct);
   }
   ```

#### Q2: ReceiveAsync()被永久阻塞
**现象**：`ReceiveAsync()`调用永不返回，即使有数据到达

**原因**：`pendingReceiveTaskSource`被其他线程覆盖，导致TaskCompletionSource无法完成

**解决方案**：
1. **确保单消费者**：同一时间只有一个`ReceiveAsync()`调用在等待
2. **使用超时取消机制**：
   ```csharp
   try
   {
       CancellationToken ct = GetTimeoutCancellableToken(); // 获取超时取消的CancellationToken
       var packet = await connection.ReceiveAsync(ct);
       // 处理数据包
   }
   catch (TimeoutException)
   {
       // 超时处理：记录日志或重试
   }
   ```
3. **检查工作器设计**：确保接收操作在专用工作任务中顺序执行

#### Q3: ReceiveOnceAsync()调用导致数据包丢失
**现象**：部分UDP数据包未被KCP处理，直接丢失

**原因**：多个未完成的`ReceiveOnceAsync()`调用竞争接收数据包

**解决方案**：
1. **使用"检查-等待-清空"模式**：
   ```csharp
   Task? udpReceiveTask = null; // 字段，或异步方法的局部变量
   
   if (udpReceiveTask?.IsCompleted ?? false)
   {
       await udpReceiveTask; // 处理已到达的数据
       udpReceiveTask = null;
   }
   
   if (udpReceiveTask == null)
   {
       udpReceiveTask = connection.ReceiveOnceAsync(ct);
   }
   ```
2. **避免并发调用**：确保同一时间只有一个`ReceiveOnceAsync()`在进行中

### 性能相关问题

#### Q4: CPU使用率过高
**现象**：KCP工作器占用大量CPU时间

**原因**：工作器循环没有适当的延迟，持续空转

**解决方案**：
```csharp
// 添加适当延迟
await Task.Delay(10, ct).ConfigureAwait(false);

// 或者根据工作负载动态调整
bool hadWork = /* 检查是否有工作处理 */;
await Task.Delay(hadWork ? 1 : 10, ct).ConfigureAwait(false);
```

**推荐延迟时间**：
- 实时应用：5-10ms
- 一般应用：10-20ms  
- 低功耗场景：20-50ms

#### Q5: 内存使用持续增长
**现象**：应用内存不断增长，最终可能耗尽

**原因**：
1. `KcpApplicationPacket`未及时释放
2. 发送队列积压未处理
3. 内部缓冲区未回收

**解决方案**：
1. **确保数据包释放**：
   ```csharp
   var packet = await connection.ReceiveAsync(ct);
   try
   {
       ProcessData(packet.Result.Buffer);
   }
   finally
   {
       packet.Dispose(); // 关键！
   }
   ```
2. **监控发送队列**：
   ```csharp
   if (connection.PacketPendingSentCount > 300)
   {
       // 发送队列积压，考虑限流或增大窗口
       LogWarning($"发送队列积压: {connection.PacketPendingSentCount}");
   }
   ```

#### Q6: KcpApplicationPacket多重释放导致数据错乱
**现象**：后续接收的数据包内容不正确或抛出异常

**原因**：`KcpApplicationPacket`是值类型，复制后多个实例共享内部缓冲区引用。提前释放会影响后续数据包。

**解决方案**：
```csharp
// ❌ 错误：复制值类型导致多重引用
var packet1 = await connection.ReceiveAsync(ct);
var packet2 = packet1; // 浅拷贝！

// ✅ 正确：立即使用或创建深拷贝
var packet = await connection.ReceiveAsync(ct);
if (packet.IsNotEmpty)
{
    // 方案1：立即使用并释放
    var data = packet.Result.Buffer;
    ProcessData(data);
    packet.Dispose();
    
    // 方案2：需要持久化时创建副本
    // byte[] copy = packet.Result.Buffer.ToArray();
    // packet.Dispose();
    // // 使用copy
}
```

#### Q7: SendAsync返回空结果但数据已排队
**现象**：`SendAsync`立即返回成功但`sentCount`为0

**原因**：在流模式下，数据被合并到前一个分片包中，没有创建新包

**解决方案**：
```csharp
var result = await connection.SendAsync(data, ct);
if (result.IsSucceed)
{
    // 只要有实际数据，即使SentCount为0，也已成功排队。可以忽略这个值
    // 我认为这是潜在的，与原始实现的行为不一致的bug，后续版本可能会更改行为
}
```

### 网络相关问题

#### Q8: 连接频繁断开或超时
**现象**：连接不稳定，经常需要重连

**原因及解决方案**：

| 可能原因 | 检查方法 | 解决方案 |
|---------|---------|----------|
| `UpdateAsync()`未定期调用 | 检查工作器是否正常执行 | 确保定期调用UpdateAsync(推荐10ms) |
| 时间戳不正确 | 检查两端时间戳算法 | 使用`DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()` |
| 网络丢包严重 | 监控重传次数 | 调整`fastresend`参数，启用快速重传 |
| 窗口大小太小 | 检查窗口使用率 | 增大`SendWindowSize`和`RecvWindowSize` |
| MTU设置不当 | 检查数据包分片 | 根据网络环境调整MTU(通常1400，根据网络特性调整（蜂窝网络、以太网等）) |

#### Q9: 高延迟环境性能差
**现象**：在高延迟网络(>100ms)中性能显著下降

**调优建议**：
```csharp
// 高延迟网络专用配置
connection.ConfigureNoDelay(
    nodelay: true,     // 启用无延迟模式
    interval: 30,      // 增加更新间隔
    resend: 3,         // 降低快速重传阈值
    nc: false          // 保持拥塞控制
);

// 增大窗口以适应高延迟
connection.SendWindowSize = 1024;
connection.RecvWindowSize = 1024;
```

### 调试建议

#### 启用基础日志
目前没有提供日志，将来会以跨平台的 System.Diagnostics.EventSource 方式提供。请注意，收集 EventSource 日志可能需要操作系统特权。
```csharp
// 修改源码，在关键位置添加条件编译的日志
[Conditional("DEBUG")]
public static void LogKcpEvent(string eventName, params object[] args)
{
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [KCP] {eventName}: {string.Join(", ", args)}");
}

// 使用示例
LogKcpEvent("PacketSent", connectionId, packetSn, length);
LogKcpEvent("PacketReceived", connectionId, packetSn, length);
LogKcpEvent("WindowChanged", connectionId, sendWindow, recvWindow);
```

### 常见错误代码

FaGe.Kcp在内部处理时可能返回以下错误码：

| 错误码 | 含义 | 可能原因 | 解决方案 |
|--------|------|----------|----------|
| **-1** | 数据包太短 | UDP数据包小于KCP头部大小(24字节) | 检查对等端实现，确保发送完整KCP数据包 |
| **-2** | 意料外的CMD | 对等端发送了无效的KCP命令 | 1. 协议版本不匹配<br>2. 数据损坏或受到攻击<br>3. 对等端不是标准KCP实现 |
| **-3** | 未知命令 | 收到无法识别的命令类型 | 检查对等端KCP实现，确保使用相同协议版本 |

### 预防措施

#### 1. 连接建立时验证
```csharp
public async Task<bool> ValidateConnectionAsync(KcpConnection connection, TimeSpan timeout)
{
    using var cts = new CancellationTokenSource(timeout);
    
    try
    {
        // 发送测试包
        var testData = Encoding.UTF8.GetBytes("TEST");
        var sendResult = await connection.SendAsync(testData, cts.Token);
        
        if (!sendResult.IsSucceed)
            return false;
        
        // 等待响应（cts带超时）
        var packet = await connection.ReceiveAsync(cts.Token)
            .WaitAsync(timeout, cts.Token);
        
        return packet.IsNotEmpty;
    }
    catch
    {
        return false;
    }
}
```

#### 2. 实现健康检查
可以在应用协议处实现健康检查（KCP协议作者推荐方案），或基于KCP连接发送健康检查包。以下是基于KCP协议发送健康检查的示例：
```csharp
public class KcpHealthChecker
{
    private readonly KcpConnection _connection;
    private DateTime _lastActivity = DateTime.UtcNow;
    
    public KcpHealthChecker(KcpConnection connection)
    {
        _connection = connection;
    }
    
    public void RecordActivity() => _lastActivity = DateTime.UtcNow;
    
    public bool IsHealthy(TimeSpan timeout)
        => DateTime.UtcNow - _lastActivity < timeout;
    
    public async Task<bool> PerformHealthCheckAsync(CancellationToken ct)
    {
        try
        {
            // 假设对等端知道['P', 'I', 'N', 'G']: byte[4]是健康检查
            var pingData = Encoding.UTF8.GetBytes("PING");
            await _connection.SendAsync(pingData, ct);
            
            var response = await _connection.ReceiveAsync(ct)
                .WaitAsync(TimeSpan.FromSeconds(3), ct);
            
            if (response.IsNotEmpty)
            {
                RecordActivity();
                return true;
            }
        }
        catch
        {
            // 健康检查失败
        }
        
        return false;
    }
}
```

#### 3. 配置合理的超时和重试
```csharp
public class ResilientKcpClient
{
    private readonly Func<Task<KcpConnection>> _connectionFactory;
    private readonly int _maxRetries;
    private readonly TimeSpan _retryDelay;
    
    public async Task<TResult> ExecuteWithRetryAsync<TResult>(
        Func<KcpConnection, CancellationToken, Task<TResult>> operation,
        CancellationToken ct)
    {
        Exception lastException = null;
        
        for (int attempt = 0; attempt < _maxRetries; attempt++)
        {
            using var connection = await _connectionFactory();
            
            try
            {
                return await operation(connection, ct);
            }
            catch (Exception ex) when (IsTransientError(ex))
            {
                lastException = ex;
                LogWarning($"尝试 {attempt + 1} 失败: {ex.Message}");
                
                if (attempt < _maxRetries - 1)
                {
                    await Task.Delay(_retryDelay, ct);
                }
            }
        }
        
        throw new InvalidOperationException("所有重试尝试都失败", lastException);
    }
    
    private static bool IsTransientError(Exception ex)
        => ex is SocketException ||
           ex is TimeoutException ||
           ex is OperationCanceledException;
}
```

## 服务端使用提示

### 连接管理建议
```csharp
public class KcpServer
{
    private readonly ConcurrentDictionary<uint, KcpConnection> _connections = new();
    private readonly KcpHealthyChecker _healthyChecker = new();
    private readonly UdpClient _udpServer;
    
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var result = await _udpServer.ReceiveAsync(ct);
            
            // 1. 判断数据包类型（握手包/数据包/健康检查包）
            if (IsHandshakePacket(result.Buffer))
            {
                // 2. 处理握手，分配唯一conv
                uint conv = AllocateConnectionId();
                var connection = new KcpConnection(_udpServer, conv, result.RemoteEndPoint);
                
                // 3. 启动连接管理
                _ = connection.RunReceiveLoop(ct);
                _connections.TryAdd(conv, connection);
                
                // 4. 发送握手响应
                SendHandshakeResponse(conv, result.RemoteEndPoint);
            }
            else if (IsHealthCheckPacket(result.Buffer))
            {
                uint conv = ExtractConvFromPacket(result.Buffer);
                if (_connections.TryGetValue(conv, out var _))
                {
                     _healthyChecker.PingReceived(conv);
                }
            }
            else
            {
                // 5. 数据包：提取conv并转发到对应连接
                uint conv = ExtractConvFromPacket(result.Buffer);
                if (_connections.TryGetValue(conv, out var conn))
                {
                    // 检查，并在必要时更新连接RemoteEndPoint
                    if (!conn.RemoteEndpoint.Equals(result.RemoteEndPoint))
                    {
                        LogInfo($"连接 {conv} 端点变更: {conn.RemoteEndpoint} -> {result.RemoteEndPoint}");
                        conn.RemoteEndpoint = result.RemoteEndPoint;
                        _healthyChecker.UpdateEndpoint(conv, result.RemoteEndPoint);
                    }
                    
                    // 然后将数据传递给连接
                    var inputResult = conn.ManualInputOnce(result);
                    
                    if (inputResult.IsFailed)
                    {
                        HandleInputFailure(conv, inputResult, result.RemoteEndPoint);
                    }
                    
                    // 记录连接活动时间
                    _healthyChecker.RecordActivity(conv);
                }
            }
        }
    }
    
    private void HandleInputFailure(uint conv, KcpInputResult inputResult, IPEndPoint remoteEndPoint)
    {
        switch (inputResult.RawResult)
        {
            case -1:  // 数据包长度不足
                LogWarning($"连接 {conv} ({remoteEndPoint}) 接收数据不完整");
                break;
            case -2:  // conv标识符不匹配
                LogWarning($"连接 {conv} ({remoteEndPoint}) 标识符不匹配");
                // 严重错误，断开连接
                if (_connections.TryRemove(conv, out var conn))
                {
                    conn.Dispose();
                    LogInfo($"已断开连接 {conv} (标识符不匹配)");
                }
                break;
            case -3:  // 意料外的CMD命令
                LogWarning($"连接 {conv} ({remoteEndPoint}) 收到无效命令");
                break;
        }
    }
}
```

### 服务端架构选项

#### 选项1：共享UdpClient（需要路由逻辑）
```csharp
// 优点：资源使用少
// 缺点：需要解析包头并路由到正确连接
// 适用：连接数少，性能要求不高

// 如上面的KcpServer示例，使用ManualInputOnce手动路由
```

#### 选项2：每个连接独立UdpClient
```csharp
// 优点：简单，无路由逻辑
// 缺点：每个连接占用一个端口
// 适用：连接数可控，需要简单实现

public class SimpleKcpServer
{
    public async Task HandleClientAsync(UdpClient client, IPEndPoint clientEndpoint, CancellationToken ct)
    {
        uint conv = AllocateConnectionId();
        var connection = new KcpConnection(client, conv, clientEndpoint);
        
        // 使用ReceiveOnceAsync自动模式
        await RunKcpWorkerAsync(connection, ct);
    }
}
```

#### 选项3：混合方案
```csharp
// 主监听端口接收握手包
// 握手成功后分配新端口创建专用连接
// 优点：平衡资源使用和复杂性
// 缺点：实现复杂，需要端口管理

public class HybridKcpServer
{
    private readonly UdpClient _mainListener;
    private readonly PortPool _portPool;
    
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var result = await _mainListener.ReceiveAsync(ct);
            
            if (IsHandshakePacket(result.Buffer))
            {
                // 分配新端口
                int port = _portPool.GetNextPort();
                var dedicatedClient = new UdpClient(port);
                
                // 启动专用连接
                _ = HandleDedicatedConnection(dedicatedClient, result.RemoteEndPoint, ct);
                
                // 发送握手响应（包含新端口信息）
                SendHandshakeResponseWithPort(result.RemoteEndPoint, port);
            }
        }
    }
}
```

### 注意事项
1. **conv分配**：确保每个客户端获得唯一的conv（connection id）
2. **连接清理**：实现心跳或超时机制，清理失效连接
3. **资源限制**：限制最大并发连接数，防止资源耗尽
4. **UDP复用**：单个UdpClient服务多个连接时，需要解析包头并路由到正确连接

**参考实现**：可参考 [neuecc/kcp](https://github.com/neuecc/kcp) 中的连接管理和握手协议设计。

## 进阶用法

### 自定义传输层实现

FaGe.Kcp允许您创建自定义的传输层实现。所有传输层都应该直接继承`KcpConnectionBase`类。

#### 设计原则

1. **一对一传输**（如WebSocket、TCP）：实现`ReceiveOnceAsync()`方法
2. **多路复用传输**（如UDP）：实现`ManualInputOnce()`方法
3. **内存传输**（测试用）：实现完整的模拟传输

#### UDP连接实现（已有）

```csharp
public sealed class KcpConnection : KcpConnectionBase
{
    private readonly UdpClient _udpTransport;
    public IPEndPoint RemoteEndpoint { get; set; }
    
    public KcpConnection(UdpClient udpTransport, uint connectionId, IPEndPoint remoteEndpoint) 
        : base(connectionId)
    {
        _udpTransport = udpTransport;
        RemoteEndpoint = remoteEndpoint;
    }
    
    protected override async ValueTask InvokeOutputCallbackAsync(
        ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        await _udpTransport.SendAsync(buffer, RemoteEndpoint, cancellationToken);
    }
    
    // 自动接收模式
    public async Task ReceiveOnceAsync(CancellationToken cancellationToken)
    {
        var result = await _udpTransport.ReceiveAsync(cancellationToken);
        var inputResult = InputFromUnderlyingTransport(result.Buffer);
        
        if (inputResult.IsFailed)
        {
            HandleInputFailure(inputResult, result.RemoteEndPoint);
        }
    }
    
    // 手动输入模式（用于服务器多路复用）
    public KcpInputResult ManualInputOnce(UdpReceiveResult receiveResult)
    {
        // 检查端点是否匹配
        if (!RemoteEndpoint.Equals(receiveResult.RemoteEndPoint))
        {
            RemoteEndpoint = receiveResult.RemoteEndPoint;
        }
        
        return InputFromUnderlyingTransport(receiveResult.Buffer);
    }
    
    private void HandleInputFailure(KcpInputResult inputResult, IPEndPoint remoteEndPoint)
    {
        switch (inputResult.RawResult)
        {
            case -1:
                Trace.WriteLine($"数据包长度不足 from {remoteEndPoint}");
                break;
            case -2:
                Trace.WriteLine($"连接标识不匹配 from {remoteEndPoint}");
                break;
            case -3:
                Trace.WriteLine($"意料外的CMD from {remoteEndPoint}");
                break;
        }
    }
}
```

### WebSocket传输层

```csharp
/// <summary>
/// WebSocket的KCP连接实现
/// </summary>
/// <remarks>
/// 每个WebSocket连接对应一个KCP连接，不支持多路复用
/// </remarks>
public sealed class WebSocketKcpConnection : KcpConnectionBase, IDisposable
{
    private readonly WebSocket _webSocket;
    private readonly ArrayPool<byte> _bufferPool;
    private readonly int _receiveBufferSize;
    private readonly IPEndPoint _logicalEndpoint;
    private bool _disposed;
    
    public IPEndPoint LogicalEndpoint => _logicalEndpoint;
    
    public WebSocketKcpConnection(
        uint conversationId, 
        WebSocket webSocket, 
        IPEndPoint logicalEndpoint,
        int receiveBufferSize = 4096) 
        : base(conversationId)
    {
        _webSocket = webSocket;
        _logicalEndpoint = logicalEndpoint;
        _bufferPool = ArrayPool<byte>.Shared;
        _receiveBufferSize = receiveBufferSize;
    }
    
    protected override async ValueTask InvokeOutputCallbackAsync(
        ReadOnlyMemory<byte> buffer, 
        CancellationToken cancellationToken)
    {
        if (_disposed || _webSocket.State != WebSocketState.Open)
            return;
            
        await _webSocket.SendAsync(
            buffer,
            WebSocketMessageType.Binary,
            endOfMessage: true,
            cancellationToken
        );
    }
    
    // WebSocket是一对一连接，只需要自动模式
    public async Task ReceiveOnceAsync(CancellationToken cancellationToken)
    {
        if (_disposed || _webSocket.State != WebSocketState.Open)
            return;
            
        var buffer = _bufferPool.Rent(_receiveBufferSize);
        
        try
        {
            var receiveResult = await _webSocket.ReceiveAsync(buffer, cancellationToken);
            
            if (receiveResult.MessageType == WebSocketMessageType.Close)
            {
                await CloseAsync(cancellationToken);
                throw new InvalidOperationException("WebSocket连接已关闭");
            }
            
            if (receiveResult.MessageType != WebSocketMessageType.Binary)
            {
                throw new InvalidOperationException("只支持二进制消息类型");
            }
            
            // 复制数据到正确大小的数组
            var data = new byte[receiveResult.Count];
            Array.Copy(buffer, 0, data, 0, receiveResult.Count);
            
            var inputResult = InputFromUnderlyingTransport(data);
            
            if (inputResult.IsFailed)
            {
                HandleWebSocketInputFailure(inputResult);
            }
        }
        finally
        {
            _bufferPool.Return(buffer);
        }
    }
    
    // WebSocket不支持多路复用，所以没有ManualInputOnce方法
    
    public async ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
            return;
            
        try
        {
            if (_webSocket.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "KCP connection closed",
                    cancellationToken
                );
            }
        }
        finally
        {
            Dispose();
        }
    }
    
    private void HandleWebSocketInputFailure(KcpInputResult inputResult)
    {
        switch (inputResult.RawResult)
        {
            case -1:
                Trace.WriteLine($"WebSocket连接 {ConnectionId} 接收数据不完整");
                break;
            case -2:
                Trace.WriteLine($"WebSocket连接 {ConnectionId} 标识符不匹配");
                break;
            case -3:
                Trace.WriteLine($"WebSocket连接 {ConnectionId} 收到无效命令");
                break;
        }
    }
    
    public void Dispose()
    {
        if (_disposed)
            return;
            
        _disposed = true;
        _webSocket.Dispose();
    }
}
```

### 内存传输层（测试用）

```csharp
/// <summary>
/// 内存模拟的KCP连接，用于单元测试和开发
/// </summary>
/// <remarks>
/// 通过MemoryTransport连接两个InMemoryKcpConnection实例
/// </remarks>
public sealed class InMemoryKcpConnection : KcpConnectionBase
{
    private readonly MemoryTransport _transport;
    private readonly object _endpoint;
    
    public object Endpoint => _endpoint;
    
    public InMemoryKcpConnection(uint conversationId, MemoryTransport transport, object endpoint) 
        : base(conversationId)
    {
        _transport = transport;
        _endpoint = endpoint;
    }
    
    protected override async ValueTask InvokeOutputCallbackAsync(
        ReadOnlyMemory<byte> buffer, 
        CancellationToken cancellationToken)
    {
        // 通过内存传输发送数据
        await _transport.SendAsync(_endpoint, buffer, cancellationToken);
    }
    
    // 自动接收模式
    public async Task ReceiveOnceAsync(CancellationToken cancellationToken)
    {
        var (data, source) = await _transport.ReceiveAsync(_endpoint, cancellationToken);
        var inputResult = InputFromUnderlyingTransport(data);
        
        if (inputResult.IsFailed)
        {
            HandleInputFailure(inputResult);
        }
    }
    
    // 手动输入模式（用于集中式路由）
    public KcpInputResult ManualInputOnce(ReadOnlyMemory<byte> data)
    {
        return InputFromUnderlyingTransport(data);
    }
    
    private void HandleInputFailure(KcpInputResult inputResult)
    {
        switch (inputResult.RawResult)
        {
            case -1:
                Trace.WriteLine($"内存连接 {ConnectionId} 接收数据不完整");
                break;
            case -2:
                Trace.WriteLine($"内存连接 {ConnectionId} 标识符不匹配");
                break;
            case -3:
                Trace.WriteLine($"内存连接 {ConnectionId} 收到无效命令");
                break;
        }
    }
}

/// <summary>
/// 内存传输，用于连接两个InMemoryKcpConnection实例
/// </summary>
public sealed class MemoryTransport : IDisposable
{
    private readonly ConcurrentDictionary<object, Channel<ReadOnlyMemory<byte>>> _channels = new();
    private readonly ConcurrentDictionary<object, object> _pairedConnections = new();
    
    public void Pair(object endpointA, object endpointB)
    {
        _pairedConnections[endpointA] = endpointB;
        _pairedConnections[endpointB] = endpointA;
        
        // 为每个端点创建Channel
        _channels.GetOrAdd(endpointA, _ => Channel.CreateUnbounded<ReadOnlyMemory<byte>>());
        _channels.GetOrAdd(endpointB, _ => Channel.CreateUnbounded<ReadOnlyMemory<byte>>());
    }
    
    public async ValueTask SendAsync(object fromEndpoint, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (_pairedConnections.TryGetValue(fromEndpoint, out var toEndpoint))
        {
            if (_channels.TryGetValue(toEndpoint, out var channel))
            {
                await channel.Writer.WriteAsync(data, ct);
            }
        }
    }
    
    public async ValueTask<(ReadOnlyMemory<byte> data, object source)> ReceiveAsync(
        object forEndpoint, 
        CancellationToken ct)
    {
        if (_channels.TryGetValue(forEndpoint, out var channel))
        {
            var data = await channel.Reader.ReadAsync(ct);
            var source = _pairedConnections[forEndpoint];
            return (data, source);
        }
        
        throw new InvalidOperationException($"Endpoint not found: {forEndpoint}");
    }
    
    public void Dispose()
    {
        foreach (var channel in _channels.Values)
        {
            channel.Writer.TryComplete();
        }
        _channels.Clear();
        _pairedConnections.Clear();
    }
}
```

#### 内存传输使用示例

```csharp
// 单元测试：内存模拟连接
[Test]
public async Task TestInMemoryKcpConnection()
{
    var transport = new MemoryTransport();
    
    // 创建两个端点
    var clientEndpoint = new object();
    var serverEndpoint = new object();
    
    // 配对连接
    transport.Pair(clientEndpoint, serverEndpoint);
    
    // 创建两个KCP连接
    var clientConn = new InMemoryKcpConnection(1, transport, clientEndpoint);
    var serverConn = new InMemoryKcpConnection(1, transport, serverEndpoint);
    
    // 配置连接
    clientConn.ConfigureNoDelay(true, 10, 2, false);
    serverConn.ConfigureNoDelay(true, 10, 2, false);
    
    // 测试发送和接收
    var sendData = Encoding.UTF8.GetBytes("Hello KCP");
    
    // 客户端发送
    await clientConn.SendAsync(sendData, CancellationToken.None);
    
    // 服务器接收
    var packet = await serverConn.ReceiveAsync(CancellationToken.None);
    Assert.IsTrue(packet.IsNotEmpty);
    Assert.AreEqual("Hello KCP", Encoding.UTF8.GetString(packet.Result.Buffer.ToArray()));
    packet.Dispose();
    
    transport.Dispose();
}
```

### 何时使用哪种模式？

| 传输类型 | 推荐模式 | 原因 |
|---------|---------|------|
| **UDP服务器** | `ManualInputOnce()` | 单个UdpClient服务多个连接，需要手动路由 |
| **UDP客户端** | `ReceiveOnceAsync()` | 每个连接有自己的UdpClient |
| **WebSocket** | `ReceiveOnceAsync()` | 一对一连接，不需要多路复用 |
| **TCP** | `ReceiveOnceAsync()` | 一对一连接 |
| **消息队列** | `ManualInputOnce()` | 集中式消息分发 |
| **内存测试** | 两种都可 | 取决于测试场景 |

### 实现要点

1. **必须实现** `InvokeOutputCallbackAsync()` 抽象方法
2. **可选实现** `ReceiveOnceAsync()` 用于自动接收
3. **可选实现** `ManualInputOnce()` 用于手动输入
4. **资源管理** 正确实现 `IDisposable` 或 `IAsyncDisposable`
5. **端点管理** 提供端点信息，支持端点变更（如UDP NAT重绑定）

### 测试建议

使用`InMemoryKcpConnection`进行单元测试：
- 模拟网络延迟和丢包
- 测试协议交互
- 验证资源管理
- 性能基准测试

## 从其他KCP实现迁移

### 主要区别

| 特性 | FaGe.Kcp | 其他实现 |
|------|----------|----------|
| **并发模型** | 非线程安全，需工作器模式 | 有些提供线程安全API |
| **异步支持** | 原生async/await | 可能使用回调或同步API |
| **资源管理** | 强调`Dispose()`模式 | 可能依赖GC |
| **API设计** | 流式API (`ReceiveAsync`) | 可能使用轮询模式 |
| **配置方式** | `ConfigureNoDelay()`方法 | 可能在构造函数中配置 |

### 常见迁移问题

1. **移除后台循环**：FaGe.Kcp没有`RunReceiveLoop()`，需要手动实现工作器
2. **处理并发安全**：必须确保单线程访问连接
3. **时间戳处理**：必须使用UTC时间戳
4. **数据包释放**：必须调用`KcpApplicationPacket.Dispose()`

## 限制和注意事项

### 已知限制

1. **最大包大小**：受`uint`类型限制，单包最大约4GB（理论值）
2. **分片数量**：受`byte frg`字段限制，最多256个分片
3. **连接ID范围**：`uint conv`，0为无效值
4. **窗口大小**：受`uint`类型限制，理论最大约42亿

### 平台特定行为

1. **UDP缓冲区大小**：不同平台默认值不同，可能需要手动调整
2. **Socket选项**：可能需要设置`ReceiveBufferSize`/`SendBufferSize`
3. **防火墙/安全软件**：可能影响UDP通信

### 性能特性

1. **内存使用**：窗口越大，内存使用越多
2. **CPU使用**：更新间隔越小，CPU使用越高
3. **网络开销**：KCP头部24字节，ACK机制有额外开销

## 资源释放

### 必须释放的资源
1. **KcpApplicationPacket** - 每次接收后必须调用`Dispose()`
2. **KcpConnection** - 连接结束时调用`Dispose()`
3. **UdpClient** - 手动管理生命周期

### 为什么只有Dispose()？
FaGe.Kcp的内部资源释放是同步操作，没有需要异步清理的资源（如文件句柄、数据库连接等）。
因此只提供`Dispose()`方法，简化API设计。

```csharp
// 简单直接的释放
connection.Dispose();
```

### 推荐模式
```csharp
// using语句
using var connection = new KcpConnection(...);
using var packet = await connection.ReceiveAsync(ct);
// 自动释放
```

## 主要API参考

### KcpConnection 主要方法
| 方法 | 参数 | 说明 | 重要行为细节 |
|------|------|------|-------------|
| `ReceiveOnceAsync(CancellationToken)` | `cancellationToken`: 取消令牌 | 从传输层接收一次数据 | 1. 异步阻塞操作，等待数据到达<br>2. 同一时间只能有一个进行中的调用<br>3. 使用"检查-等待-清空"模式避免数据包丢失 |
| `ManualInputOnce(UdpReceiveResult)` | `receiveResult`: UDP接收结果 | 手动输入外部接收的数据 | 1. 用于多路复用场景<br>2. 同步方法，立即返回<br>3. 检查并更新RemoteEndpoint |
| `UpdateAsync(uint timestamp, CancellationToken)` | `timestamp`: **当前UTC时间戳(毫秒)**<br>`cancellationToken`: 取消令牌 | 更新KCP内部状态 | 1. **必须定期调用**（推荐10ms间隔）<br>2. 时间戳必须使用UTC时间<br>3. 内部可能调用`FlushAsync()`发送数据 |
| `SendAsync(ReadOnlyMemory<byte>, CancellationToken)` | `buffer`: 要发送的数据<br>`cancellationToken`: 取消令牌 | 发送应用层数据，返回发送结果 | 1. 在流模式下可能返回`sentCount: 0`（数据合并到前一个包）<br>2. 立即返回，实际发送由后续`UpdateAsync()`触发<br>3. 返回的`Task`在数据放入发送队列时完成，不代表网络发送完成 |
| `ReceiveAsync(CancellationToken)` | `cancellationToken`: 取消令牌 | 接收应用层数据包，返回`KcpApplicationPacket` | 1. 返回的`KcpApplicationPacket`必须在处理完成后立即`Dispose()`<br>2. 空数据包(`IsEmpty: true`)不需要释放<br>3. 同一时间只能有一个等待中的调用 |
| `Dispose()` | 无 | 释放连接资源 | 1. 同步释放所有内部资源<br>2. 取消所有挂起的异步操作<br>3. 确保在所有操作完成后调用 |
| `ConfigureNoDelay(bool, int, int, bool)` | `nodelay`: 是否启用无延迟<br>`interval`: 更新间隔(ms)<br>`resend`: 快速重传阈值<br>`nc`: 是否关闭拥塞控制 | 配置协议参数 | 1. 线程安全，可在初始化时调用<br>2. 后续调用需要考虑同步<br>3. 推荐配置：`(true, 10, 2, false)`用于低延迟 |

### KcpConnection 主要属性
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

### KcpApplicationPacket 主要成员
| 成员 | 类型 | 说明 |
|------|------|------|
| `Result` | `ReadResult` | 核心读取结果，通过`Result.Buffer`获取数据 |
| `IsEmpty` | `bool` | 判断数据包是否为空 |
| `IsNotEmpty` | `bool` | 判断数据包是否非空 |
| `Dispose()` | `void` | 释放数据包资源，**必须调用** |

### KcpConnection 线程安全级别

| 方法 | 线程安全 | 并发使用限制 |
|------|----------|--------------|
| `SendAsync()` | ❌ 不安全 | 必须通过专用工作器或同步机制调用 |
| `ReceiveAsync()` | ❌ 不安全 | 同一时间只能有一个等待中的调用 |
| `UpdateAsync()` | ❌ 不安全 | 不能与其他方法并发调用 |
| `ReceiveOnceAsync()` | ❌ 不安全 | 同一时间只能有一个进行中的调用 |
| `ManualInputOnce()` | ❌ 不安全 | 不能与其他方法并发调用 |
| `Dispose()` | ⚠️ 有条件 | 在所有操作完成后调用，与其他操作并发可能导致异常 |
| `ConfigureNoDelay()` | ✅ 安全 | 可在初始化时调用，后续调用需同步 |

## 许可证
本项目基于 MIT 许可证开源。

---

**注意**：本README中的示例代码仅供参考，实际使用时请根据具体需求调整错误处理、资源管理和启动停止逻辑。对于生产环境使用，建议充分测试并发安全性和异常处理逻辑。
