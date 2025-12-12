using System;
using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace FaGe.Kcp.Connections;

public sealed class KcpConnection(UdpClient udpTransport, uint connectionId) : KcpConnectionBase(connectionId)
{	
	private readonly UdpClient udpTransport = udpTransport;

	/// <summary>
	/// 创建一个KCP连接，根据<paramref name="isBindEndpoint"/>参数决定UDP传输的配置方式。
	/// 此构造函数用于简化KCP连接的创建，允许通过单个端点参数和标志来配置连接模式。
	/// </summary>
	/// <param name="listeningOrConnectEndpoint">
	/// 端点地址，具体含义取决于<paramref name="isBindEndpoint"/>的值：<p/>
	/// - 当<paramref name="isBindEndpoint"/>为true时，表示本地监听地址（IP和端口）<p/>
	/// - 当<paramref name="isBindEndpoint"/>为false时，表示远程连接地址（IP和端口）
	/// </param>
	/// <param name="isBindEndpoint">
	/// 指示端点用途的标志：<p/>
	/// - true：将UDP套接字绑定到<paramref name="listeningOrConnectEndpoint"/>指定的本地地址（服务端模式）<p/>
	/// - false：将UDP套接字连接到<paramref name="listeningOrConnectEndpoint"/>指定的远程地址（客户端模式）
	/// </param>
	/// <param name="connectionId">KCP连接的唯一标识符</param>
	public KcpConnection(IPEndPoint listeningOrConnectEndpoint, bool isBindEndpoint, uint connectionId) : this(new UdpClient(), connectionId)
	{
		if (!isBindEndpoint)
			udpTransport.Connect(listeningOrConnectEndpoint);  // 客户端模式：连接到远程端点
		else
			udpTransport.Client.Bind(listeningOrConnectEndpoint);  // 服务端模式：绑定到本地端点
	}


	protected sealed override ValueTask InvokeOutputCallbackAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
	{
		return UdpOutputAsync(buffer, cancellationToken);
	}

	private ValueTask UdpOutputAsync(ReadOnlyMemory<byte> memory, CancellationToken cancellationToken)
	{
		Debug.Assert(udpTransport != null);
		// udp发送不管成功与否都丢弃发送数据长度，因为udp不保证数据送达
		if (!memory.IsEmpty)
		{
			ValueTask<int> sendTask = udpTransport.SendAsync(memory, cancellationToken);

			if (sendTask.IsCompleted)
			{
				// 调用GetResult()通知任务完成
				_ = sendTask.GetAwaiter().GetResult();

				return ValueTask.CompletedTask;
			}
			else
			{
				return DiscardBytesSent(sendTask);
			}
		}
		else
		{
			// 没有数据
			Trace.WriteLine("[FaGe.KCP] 在没有数据的情况下调用了InvokeOutputCallbackAsync");
			return ValueTask.CompletedTask;
		}

		static async ValueTask DiscardBytesSent(ValueTask<int> discarding) => _ = await discarding;
	}
}
