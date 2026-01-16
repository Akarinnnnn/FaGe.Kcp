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

	public async Task RunReceiveLoop(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			UdpReceiveResult result = await udpTransport.ReceiveAsync(cancellationToken);
			KcpInputResult kcpInputResult = InputFromUnderlyingTransport(result.Buffer);
			if (kcpInputResult.IsFailed)
			{
				switch (kcpInputResult.RawResult)
				{
					case -2:
						Trace.WriteLine("出现了意料外的CMD，可能是因为对等端不是标准KCP实现");
						break;
					case -1:
						Trace.WriteLine("输入数据包太短");
						break;
					default:
						break;
				}
			}
		}
	}

	public ValueTask<KcpSendResult> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			UdpReceiveResult result = await udpTransport.ReceiveAsync(cancellationToken);
			KcpInputResult kcpInputResult = InputFromUnderlyingTransport(result.Buffer);
			if (kcpInputResult.IsFailed)
			{
				// ETW
				Trace.WriteLine($"[FaGe.KCP] KCP连接（ID={ConnectionId}）接收数据失败，错误码：{kcpInputResult.RawResult}");
			}
		}
	}

	public ValueTask<KcpSendResult> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) => SendAsyncBase(buffer, cancellationToken);
	public ValueTask<KcpApplicationPacket> ReceiveAsync(CancellationToken cancellationToken) => ReceiveAsyncBase(cancellationToken);
}
