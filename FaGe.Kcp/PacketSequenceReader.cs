using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text;

namespace FaGe.Kcp;

internal class PacketSequenceReader
{
	private readonly PacketSequence parent;
	private readonly PipeReader bufferReader;

	private ReadResult? bufferReadResult;

	public PacketSequenceReader(PacketSequence parent)
	{
		this.parent = parent;
		bufferReader = parent.SendBufferPipe.Reader;
	}

	public bool TryBeginRead()
	{
		if (!bufferReader.TryRead(out var bufferResult))
			return false;

		bufferReadResult = bufferResult;
	}

	public void EndRead()
	{
		if (bufferReadResult != null)
		{
			bufferReader.AdvanceTo()
		}

		bufferReadResult = null;
	}
}
