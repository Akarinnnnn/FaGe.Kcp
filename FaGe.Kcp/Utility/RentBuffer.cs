using FaGe.Kcp.Tracing;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace FaGe.Kcp.Utility
{
	public struct RentBuffer(int size, ArrayPool<byte> source) : IDisposable
	{
		private readonly ArrayPool<byte> source = source;

		public byte[]? Buffer { get; private set; } = DoInitialRent(size, source);

		private static byte[] DoInitialRent(int size, ArrayPool<byte> source)
		{
			var buffer = source.Rent(size);

			if (KcpTraceEventSource.Log.IsVerboseEnabled(KcpTraceEventSource.KcpEventKeywords.Internal))
				KcpTraceEventSource.Log.KcpBufferWasRent(0, size, buffer.Length);
			
			return buffer;
		}

		public readonly int Capacity
		{
			get
			{
				ObjectDisposedException.ThrowIf(Buffer == null, typeof(RentBuffer));

				return Buffer.Length;
			}
		}

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
			if (KcpTraceEventSource.Log.IsVerboseEnabled(KcpTraceEventSource.KcpEventKeywords.Internal))
				KcpTraceEventSource.Log.KcpBufferWasRent(Buffer.Length, newSize, buffer.Length);

				Buffer.AsSpan().CopyTo(buffer.AsSpan());
				Dispose();
				Buffer = buffer;
			}
		}
	}
}