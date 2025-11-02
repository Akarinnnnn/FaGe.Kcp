using FaGe.Kcp.Connections;
using System.Buffers;
using System.IO.Pipelines;

namespace FaGe.Kcp;

internal class SendingPacketSequenceWriter : PipeWriter
{
	private readonly PipeWriter underlying;
	private readonly PacketSequence sequence;
	// 用来填充头部信息
	private readonly KcpConnectionBase connection;
	private int packetLength;
	private Memory<byte> headerBuffer;

	internal SendingPacketSequenceWriter(PacketSequence parent, KcpConnectionBase connection)
	{
		sequence = parent;
		this.connection = connection;
		underlying = parent.SendBufferPipe.Writer;
	}

	public override ValueTask<FlushResult> FlushAsync(CancellationToken ct)
	{
		ValidateState();

		if (ct.IsCancellationRequested)
			return ValueTask.FromResult(new FlushResult(true, false));

		//connection.PrepareSendingPacketHeader(packetLength)
		//	.WithLength(packetLength)
		//	.Write(headerBuffer.Span);

		sequence.CommitPacket(packetLength);

		Reset();

		return underlying.FlushAsync(ct);
	}

	public override void Complete(Exception? exception = null)
	{
		underlying.Complete(exception);
	}

	public override ValueTask CompleteAsync(Exception? exception = null)
	{
		return underlying.CompleteAsync(exception);
	}

	public override void CancelPendingFlush()
	{
		ValidateState();

		underlying.CancelPendingFlush();
	}

	private void Reset()
	{
		ValidateState();

		headerBuffer = default; // 重置包头缓冲区引用，准备开始下一次写入
		packetLength = 0;
	}

	public override void Advance(int count)
	{
		ValidateState();

		underlying.Advance(count + KcpPacketHeaderAnyEndian.ExpectedSize);
		packetLength += count;
	}


	public override Memory<byte> GetMemory(int sizeHint = 0)
	{
		ValidateState();

		// 取出足够存放数据包头和内容的缓冲区
		Memory<byte> bufferFromPipe = underlying.GetMemory(sizeHint + KcpPacketHeaderAnyEndian.ExpectedSize);
		if (headerBuffer.Length != 0)
		{
			headerBuffer = bufferFromPipe[..KcpPacketHeaderAnyEndian.ExpectedSize];
			return bufferFromPipe[KcpPacketHeaderAnyEndian.ExpectedSize..];
		}
		else
		{
			return bufferFromPipe;
		}
	}

	public override Span<byte> GetSpan(int sizeHint = 0)
	{
		ValidateState();
		return GetMemory(sizeHint).Span;
	}

	private void ValidateState()
	{
		var connectionState = connection.State;
		if (connectionState == KcpConnectionState.Connected)
			return;

		// TODO
	}
}
