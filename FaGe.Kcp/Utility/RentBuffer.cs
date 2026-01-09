using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace FaGe.Kcp.Utility
{
	public struct RentBuffer(int size, ArrayPool<byte> source) : IDisposable
	{
		private readonly ArrayPool<byte> source = source;

		public byte[]? Buffer { get; private set; } = source.Rent(size);

		public readonly Span<byte> Span
		{
			get
			{
				ObjectDisposedException.ThrowIf(Buffer == null, typeof(RentBuffer));

				return Buffer.AsSpan();
			}
		}

		public readonly Memory<byte> Memory
		{
			get
			{
				ObjectDisposedException.ThrowIf(Buffer == null, typeof(RentBuffer));

				return Buffer.AsMemory();
			}
		}
		public void Dispose()
		{
			if (Buffer == null)
				return;

			source.Return(Buffer);
			Buffer = null;
		}

		public void EnsureCapacity(int newSize)
		{
			ObjectDisposedException.ThrowIf(Buffer == null, typeof(RentBuffer));

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