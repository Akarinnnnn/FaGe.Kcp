using FaGe.Kcp.Connections;
using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text;

namespace FaGe.Kcp;

// 不学Pipe了，功能不写在这个文件
internal class PacketSequence
{
	internal Pipe SendBufferPipe { get; } = new Pipe(new(
		readerScheduler: PipeScheduler.ThreadPool,
		writerScheduler: PipeScheduler.ThreadPool,
		minimumSegmentSize: KcpPacketHeaderAnyEndian.ExpectedSize,
		resumeWriterThreshold: KcpPacketHeaderAnyEndian.ExpectedSize
	));

	private Queue<int> packetLengthQueue;
	private readonly KcpConnectionBase connection;

	public SendingPacketSequenceWriter Writer { get; }
	public PacketSequenceReader Reader { get; }

	internal PacketSequence(KcpConnectionBase connection)
	{
		Writer = new(this, connection);
		Reader = new(this);
		packetLengthQueue = new Queue<int>(20);
		this.connection = connection;
	}

	internal void CommitPacket(int packetLength)
	{
		packetLengthQueue.Enqueue(packetLength);
	}

	internal int ConsumePacket()
	{
		return packetLengthQueue.Dequeue();
	}

	internal IReadOnlyCollection<int> EnumeratePackets()
	{
		return packetLengthQueue;
	}
}
