using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace FaGe.Kcp.Utility
{
	internal struct RentBuffer : IDisposable
	{
		private readonly ArrayPool<byte> source;

		public RentBuffer(int size, ArrayPool<byte> source)
		{
			Buffer = source.Rent(size);
			this.source = source;
		}
		
		public byte[] Buffer { get; private set; }

		public Span<byte> Span => Buffer.AsSpan();

		public Memory<byte> Memory => Buffer.AsMemory();

		public void Dispose()
		{
			source.Return(Buffer);
		}

		public void EnsureCapacity(int newSize)
		{
			if (Buffer.Length < newSize)
			{ 
				var buffer = source.Rent(newSize);
				Buffer.AsSpan().CopyTo(buffer.AsSpan());
				Dispose();
				Buffer = buffer;
			}
		}
	}
}
