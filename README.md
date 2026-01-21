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
- [故障排除](#故障排除)
  - [常见问题](#常见问题)
  - [性能调优](#性能调优)
- [服务端使用提示](#服务端使用提示)
- [进阶用法](#进阶用法)
  - [自定义传输层](#自定义传输层)
- [资源释放](#资源释放)
- [主要API参考](#主要api参考)
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
    
    // 并行启动所有任务
    var tasks = new[]
    {
        connection.RunReceiveLoop(ct),
        RunUpdateLoop(connection, ct),
        RunSendLoop(connection, ct),
        RunReceiveLoop(connection, ct)
    };
    
    await Task.WhenAll(tasks);
}

async Task RunUpdateLoop(KcpConnection connection, CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        // ✅ 重要：KCP协议需要UTC时间戳，确保网络两端时间可比
        // 不要使用 Environment.TickCount64（不同机器启动时间不同）
        uint timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await connection.UpdateAsync(timestamp, ct);
        await Task.Delay(10, ct);
    }
}

async Task RunSendLoop(KcpConnection connection, CancellationToken ct)
{
    int count = 0;
    while (!ct.IsCancellationRequested)
    {
        var data = Encoding.UTF8.GetBytes($"Message {count++}");
        await connection.SendAsync(data, ct);
        await Task.Delay(1000, ct);
    }
}

async Task RunReceiveLoop(KcpConnection connection, CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        var packet = await connection.ReceiveAsync(ct);
        if (packet.IsNotEmpty)
        {
            // 最优方案：直接使用Buffer，处理完成后立即释放
            var buffer = packet.Result.Buffer;
            ProcessData(buffer);
            packet.Dispose();
        }
        await Task.Delay(1, ct);
    }
}
```

### 并行架构说明
FaGe.Kcp设计为并行执行多个操作：

```
┌─────────────────┐
│  RunReceiveLoop │  ← UDP数据接收（后台）
├─────────────────┤
│  UpdateLoop     │  ← KCP状态更新（定期10ms，使用UTC时间戳）
├─────────────────┤
│  SendLoop       │  ← 应用数据发送
├─────────────────┤
│  ReceiveLoop    │  ← 应用数据接收
└─────────────────┘
```

**最佳实践**：
1. 按顺序启动所有循环任务，启动完毕前不要等待其中任何一个任务
2. 任务启动完毕后使用`Task.WhenAll`等待所有任务完成
3. 在finally块中确保资源释放
4. 接收数据处理完成后立即调用`packet.Dispose()`

## 核心概念

### KcpApplicationPacket
KCP应用层数据包载体，**必须在使用后立即释放**：

```csharp
// ✅ 最优方案
var packet = await connection.ReceiveAsync(ct);
if (packet.IsNotEmpty)
{
    var buffer = packet.Result.Buffer; // 直接使用，无需拷贝
    ProcessData(buffer);
    packet.Dispose(); // 立即释放
}

// ✅ 需要持久化时
var packet = await connection.ReceiveAsync(ct);
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
- **异步友好**：真正的异步API，支持CancellationToken
- **资源自动管理**：内部缓冲区使用ArrayPool，减少GC压力

### 配置选项
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

## 故障排除

### 常见问题

#### Q1: KcpApplicationPacket多重释放问题
**问题**：由于`KcpApplicationPacket`包含对内部缓冲区的引用，如果同时持有多个实例并分别释放，会导致多重释放。

**解决方案**：
- 设计为一次性读取、立即释放
- 如需持久化数据，请调用`.ToArray()`获取副本
- 避免复制同一个数据包实例

#### Q2: 发送数据后收不到ACK
**可能原因**：
1. 接收窗口已满，对端停止发送ACK
2. `UpdateAsync`未正确执行
3. 网络丢包严重

**解决方案**：
1. 检查接收方窗口状态
2. 确保定期调用`UpdateAsync`（推荐10-50ms间隔）
3. 调整`fastresend`参数启用快速重传

#### Q3: 内存泄漏或缓冲区累积
**监控指标**：
```csharp
// 监控发送队列状态
int pendingCount = connection.PacketPendingSentCount;
if (pendingCount > 300) // 阈值根据应用调整
{
    // 考虑限流或调整窗口大小
}
```

### 性能调优
- **时间戳**：**必须使用基于UTC的时间戳，或至少两端时间戳算法一致**：
  ```csharp
  // ✅ 正确：使用UTC时间戳，确保两端时间可比
  uint timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
  
  // ❌ 错误：不同机器启动时间不同，无法比较
  uint timestamp = (uint)Environment.TickCount64;
  ```
  
- **MTU设置**：**默认1400字节**（基于标准以太网MTU 1500减去IP/UDP头部预留）
- **窗口大小**：根据网络延迟和带宽调整
- **更新间隔**：通常10ms，高延迟网络可适当增加

### 调试建议
FaGe.Kcp计划使用`EventSource`提供跨平台的高性能内核级跟踪（Windows ETW / Linux LTTng）。当前版本可通过修改源码，使用条件编译输出调试信息：

```csharp
// 条件编译的调试输出
[Conditional("DEBUG")]
public static void LogPacket(string eventType, uint conv, uint sn, int length)
{
#if DEBUG
    Console.WriteLine($"[KCP-{eventType}] Conv:{conv} SN:{sn} Len:{length}");
#endif
}
```

## 服务端使用提示

FaGe.Kcp 主要提供连接级别的实现，服务端需要自行管理多个客户端连接：

### 连接管理建议
```csharp
public class KcpServer
{
    private readonly ConcurrentDictionary<uint, KcpConnection> _connections = new();
    private readonly UdpClient _udpServer;
    
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var result = await _udpServer.ReceiveAsync(ct);
            
            // 1. 判断数据包类型（握手包 or 数据包）
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
            else
            {
                // 5. 数据包：提取conv并转发到对应连接
                uint conv = ExtractConvFromPacket(result.Buffer);
                if (_connections.TryGetValue(conv, out var conn))
                {
                    // 注意：需要实现将数据输入到连接的方法
                    // 或者让每个连接有自己的UdpClient
                }
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

### 自定义传输层
通过继承`KcpConnectionBase`实现自定义传输：

```csharp
// ✅ 推荐：标记为sealed，防止进一步继承带来的复杂性，以及触发潜在的JIT优化
public sealed class CustomKcpConnection : KcpConnectionBase
{
    private readonly ITransport _transport;
    
    public CustomKcpConnection(uint conversationId, ITransport transport) 
        : base(conversationId)
    {
        _transport = transport;
    }
    
    protected sealed override async ValueTask InvokeOutputCallbackAsync(
        ReadOnlyMemory<byte> buffer, 
        CancellationToken cancellationToken)
    {
        // 实现自定义传输逻辑
        await _transport.SendAsync(buffer, cancellationToken);
    }
    
    // 从自定义传输接收数据的方法
    public async Task ReceiveOnceAsync(CancellationToken cancellationToken)
    {
        // 伪代码：从你的传输层提取数据
        // var data = await YourTransport.ReceiveDataAsync(cancellationToken);
        // InputFromUnderlyingTransport(data);
        
        // 示例实现取决于具体传输机制
        // - 消息队列: await _queue.ReceiveAsync(ct)
        // - WebSocket: await _socket.ReceiveAsync(_buffer, ct)
        // - 命名管道: await _pipe.ReadAsync(_buffer, ct)
    }
}
```

**为什么推荐sealed？**
1. **简化设计**：KCP连接通常是叶子节点，不需要进一步继承
2. **性能优化**：sealed类支持更好的JIT优化
3. **避免意外**：防止子类覆盖关键方法导致协议行为异常
4. **明确的职责**：每个连接类型有明确的传输方式

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
| 方法 | 参数 | 说明 |
|------|------|------|
| `RunReceiveLoop(CancellationToken)` | `cancellationToken`: 取消令牌 | 启动UDP接收后台循环 |
| `UpdateAsync(uint timestamp, CancellationToken)` | `timestamp`: **当前UTC时间戳(毫秒)**<br>`cancellationToken`: 取消令牌 | 更新KCP内部状态，**必须定期调用**（推荐10ms间隔） |
| `SendAsync(ReadOnlyMemory<byte>, CancellationToken)` | `buffer`: 要发送的数据<br>`cancellationToken`: 取消令牌 | 发送应用层数据，返回发送结果 |
| `ReceiveAsync(CancellationToken)` | `cancellationToken`: 取消令牌 | 接收应用层数据包，返回`KcpApplicationPacket` |
| `Dispose()` | 无 | 释放连接资源 |
| `ConfigureNoDelay(bool, int, int, bool)` | `nodelay`: 是否启用无延迟<br>`interval`: 更新间隔(ms)<br>`resend`: 快速重传阈值<br>`nc`: 是否关闭拥塞控制 | 配置协议参数 |

### KcpConnection 主要属性
| 属性 | 类型 | 说明 |
|------|------|------|
| `ConnectionId` | `int` | 连接标识符 |
| `RemoteEndpoint` | `IPEndPoint` | 远程端点 |
| `IsStreamMode` | `bool` | 是否启用流模式 |
| `MTU` | `uint` | 最大传输单元，**默认1400字节** |
| `SendWindowSize` | `int` | 发送窗口大小 |
| `RecvWindowSize` | `int` | 接收窗口大小 |
| `PacketPendingSentCount` | `int` | 待发送数据包数量 |

### KcpApplicationPacket 主要成员
| 成员 | 类型 | 说明 |
|------|------|------|
| `Result` | `ReadResult` | 核心读取结果，通过`Result.Buffer`获取数据 |
| `IsEmpty` | `bool` | 判断数据包是否为空 |
| `IsNotEmpty` | `bool` | 判断数据包是否非空 |
| `Dispose()` | `void` | 释放数据包资源，**必须调用** |

## 许可证
本项目基于 MIT 许可证开源。

---

**注意**：本README中的示例代码仅供参考，实际使用时请根据具体需求调整错误处理、资源管理和启动停止逻辑。
