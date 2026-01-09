using System;
using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace FaGe.Kcp.Connections;

public sealed class KcpConnection(UdpClient udpTransport, uint connectionId, IPEndPoint remoteEndpoint) : KcpConnectionBase(connectionId)
{
	private readonly UdpClient udpTransport = udpTransport;

	public IPEndPoint RemoteEndpoint => remoteEndpoint;

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
			ValueTask<int> sendTask = udpTransport.SendAsync(memory, remoteEndpoint, cancellationToken);

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

	public ValueTask<KcpSendResult> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
	{
		return SendAsyncBase(buffer, cancellationToken);
	}

	public ValueTask<KcpApplicationPacket> ReceiveAsync(CancellationToken cancellationToken = default)
	{
		return ReceiveAsyncBase(cancellationToken);
	}
}