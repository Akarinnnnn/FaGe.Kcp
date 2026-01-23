using System;
using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace FaGe.Kcp.Connections;

public sealed class KcpConnection(UdpClient udpTransport, uint connectionId, IPEndPoint remoteEndpoint) : KcpConnectionBase(connectionId)
{
	private readonly UdpClient udpTransport = udpTransport;

	public IPEndPoint RemoteEndpoint { get; set; } = remoteEndpoint;

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
			ValueTask<int> sendTask = udpTransport.SendAsync(memory, RemoteEndpoint, cancellationToken);

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

	public async Task ReceiveOnceAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		UdpReceiveResult result = await udpTransport.ReceiveAsync(cancellationToken);
		KcpInputResult kcpInputResult = InputFromUnderlyingTransport(result.Buffer);
		if (kcpInputResult.IsFailed)
		{
			switch (kcpInputResult.RawResult)
			{
				case -3:
					Trace.WriteLine("出现了意料外的CMD，可能是因为对等端不是标准KCP实现");
					break;
				case -2:
					Trace.WriteLine("数据包校验失败，连接标识不匹配");
					break;
				case -1:
					Trace.WriteLine("输入数据不够（不足一个包头大小，或KCP数据包不完整）");
					break;
				default:
					break;
			}
		}
	}

	/// <summary>
	/// 手动从外部的底层传输中，输入一个数据包
	/// </summary>
	/// <param name="receiveResult">外部托管的底层传输中，获得的数据包</param>
	/// <returns></returns>
	public KcpInputResult ManualInputOnce(UdpReceiveResult receiveResult)
	{
		return InputFromUnderlyingTransport(receiveResult.Buffer);
	}

	public ValueTask<KcpSendResult> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) => SendAsyncBase(buffer, cancellationToken);
	public ValueTask<KcpApplicationPacket> ReceiveAsync(CancellationToken cancellationToken) => ReceiveAsyncBase(cancellationToken);
}
