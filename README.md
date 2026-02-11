# FaGe.Kcp

FaGe.Kcp 是一个基于 KCP 协议实现库，专注于简化 KCP 协议在 .NET 环境下的使用，提供原生的连接管理、数据包收发、资源自动释放等核心能力。此外，还提供了原生异步收发API(async-await模式)。

## 📚 文档目录

详细的文档已拆分到 `./docs/` 目录中，按章节组织：

- [01_时间戳使用说明.md](./docs/01_时间戳使用说明.md) - KCP协议时间戳使用指南
- [02_快速开始.md](./docs/02_快速开始.md) - 最小示例和并行架构
- [03_核心概念.md](./docs/03_核心概念.md) - KcpApplicationPacket、KcpSendResult等核心概念
- [04_实现细节.md](./docs/04_实现细节.md) - 架构分层和配置选项
- [05_并发安全性.md](./docs/05_并发安全性.md) - 线程安全级别和最佳实践
- [06_故障排除.md](./docs/06_故障排除.md) - 常见问题解决方案
- [07_服务端使用提示.md](./docs/07_服务端使用提示.md) - 服务端架构和连接管理
- [08_进阶用法.md](./docs/08_进阶用法.md) - 自定义传输层实现
- [09_从其他KCP实现迁移.md](./docs/09_从其他KCP实现迁移.md) - 迁移指南
- [10_限制和注意事项.md](./docs/10_限制和注意事项.md) - 已知限制和平台特性
- [11_资源释放.md](./docs/11_资源释放.md) - 资源管理和Dispose模式
- [12_主要API参考.md](./docs/12_主要API参考.md) - 完整API文档

## 🚀 快速开始（摘要）

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
    
    // 使用Channel进行线程安全的发送
    var sendChannel = System.Threading.Channels.Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
    
    // 启动工作器任务
    var workerTask = KcpWorkerAsync(connection, sendChannel, ct);
    // ... 更多代码
}
```

**完整示例请查看：[02_快速开始.md](./docs/02_快速开始.md)**

## 💡 核心概念（摘要）

### KcpApplicationPacket
KCP应用层数据包载体，**必须在使用后立即释放**：

```csharp
var packet = await connection.ReceiveAsync(ct);
if (packet.IsNotEmpty)
{
    ProcessData(packet.Result.Buffer);
    packet.Dispose(); // 立即释放
}
```

### 流模式 vs 包模式
| 特性 | 包模式 (默认) | 流模式 |
|------|---------------|--------|
| 消息边界 | 保持完整边界 | 可能拆包/粘包 |
| 适用场景 | 消息通信、RPC | 文件传输、流媒体 |

**完整概念请查看：[03_核心概念.md](./docs/03_核心概念.md)**

## ⚠️ 重要注意事项

1. **并发安全性**：FaGe.Kcp 大多数核心方法**不是线程安全的**，必须使用专用工作器模式
2. **时间戳**：必须使用UTC时间戳确保两端一致性
3. **资源释放**：`KcpApplicationPacket` 必须在使用后立即调用 `Dispose()`
4. **定期更新**：必须定期调用 `UpdateAsync()`（推荐10ms间隔）

## 📖 详细文档

所有详细文档已按章节拆分到 `./docs/` 目录中，每个文件对应一个主题：

```
docs/
├── 01_时间戳使用说明.md
├── 02_快速开始.md
├── 03_核心概念.md
├── 04_实现细节.md
├── 05_并发安全性.md
├── 06_故障排除.md
├── 07_服务端使用提示.md
├── 08_进阶用法.md
├── 09_从其他KCP实现迁移.md
├── 10_限制和注意事项.md
├── 11_资源释放.md
└── 12_主要API参考.md
```

## 📄 许可证

本项目基于 MIT 许可证开源。

---

**注意**：本README中的示例代码仅供参考，实际使用时请根据具体需求调整错误处理、资源管理和启动停止逻辑。对于生产环境使用，建议充分测试并发安全性和异常处理逻辑。

详细文档请查看 `./docs/` 目录中的相应章节。

---
本文完全由人工编写，没有机器人参与🤖
IP：生化斯坦
