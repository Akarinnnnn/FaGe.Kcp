using System;
using System.Buffers;
using System.Diagnostics;
using System.Net.Sockets;

namespace FaGe.Kcp.Connections;

public sealed class KcpConnection(UdpClient udpTransport) : KcpConnectionBase(true)
{	
	private readonly UdpClient udpTransport = udpTransport;

	public KcpConnection(Socket baseSocket) : this(new UdpClient() { Client = baseSocket })
	{
		
	}

	private protected sealed override ValueTask InvokeOutputCallbackAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
	{
		return UdpOutputAsync(buffer, cancellationToken);
	}

	private ValueTask UdpOutputAsync(ReadOnlySequence<byte> sequence, CancellationToken cancellationToken)
	{
		Debug.Assert(udpTransport != null);
		// 为一次送出全部数据优化的快路径: sequence非空且只有一段
		if (!sequence.IsEmpty)
		{
			ReadOnlyMemory<byte> buffer = sequence.First;
			if (sequence.IsSingleSegment)
			{
				ValueTask<int> sendTask = udpTransport.SendAsync(buffer, cancellationToken);

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
				return UdpOutputAsyncCore(sequence, cancellationToken);
			}
		}
		else
		{
			// 没有数据
			Debug.WriteLine("[FaGe.KCP] 在没有数据的情况下调用了InvokeOutputCallbackAsync");
			return ValueTask.CompletedTask;
		}

		static async ValueTask DiscardBytesSent(ValueTask<int> discarding) => _ = await discarding;

		// 处理复杂情况
		async ValueTask UdpOutputAsyncCore(ReadOnlySequence<byte> sequence, CancellationToken cancellationToken)
		{
			if (sequence.IsSingleSegment)
			{
				_ = await udpTransport.SendAsync(sequence.First, cancellationToken);
				return;
			}

			if (sequence.Length > MTU)
			{
				throw new ArgumentException($"待发送数据的长度超过了上限", nameof(sequence));
			}

			byte[] tempBufferArray = AckOutputTemporaryBufferPool.Rent((int)MTU);
			Memory<byte> tempBuffer = tempBufferArray[..(int)MTU];

			try
			{
				Memory<byte> workingSpace = tempBuffer;
				foreach (var buffer in sequence)
				{
					cancellationToken.ThrowIfCancellationRequested();
					buffer.CopyTo(workingSpace[..buffer.Length]);
					workingSpace = workingSpace[buffer.Length..];
				}
				
				_ = await udpTransport.SendAsync(tempBuffer, cancellationToken);
			}
			finally
			{
				AckOutputTemporaryBufferPool.Return(tempBufferArray);
			}
		}
	}
}
