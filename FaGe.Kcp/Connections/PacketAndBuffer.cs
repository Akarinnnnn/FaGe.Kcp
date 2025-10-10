using FaGe.Kcp.Utility;
using System.Buffers;
using System.Diagnostics;
using static FaGe.Kcp.KcpConst;

namespace FaGe.Kcp.Connections
{
	// 我在想struct是不是更好一些，或者class方便修改和传递引用
	internal struct PacketAndBuffer(KcpPacketHeader header, ArrayPool<byte> bufferSource)
		: IDisposable
	{
		private RentBuffer rentBuffer = new(0, bufferSource);

		public readonly Memory<byte> RentBuffer => rentBuffer.Memory;

		public KcpPacketHeader Header = header; // 不readonly，因为构造后可能需要修改
		public int WritingBeginOffset;
		public int Length;

		// 这些也是会变的，在FlushAsync里会更新
		public uint resendts;
		public uint rto;
		public uint fastack;
		public uint xmit;

		public PacketAndBuffer(KcpPacketHeader header) : this(header, ArrayPool<byte>.Shared)
		{
		}

		public readonly Memory<byte> Memory => RentBuffer.Slice(IKCP_OVERHEAD, Length);
		public readonly Memory<byte> RemainingMemory => RentBuffer[(WritingBeginOffset + IKCP_OVERHEAD + Length)..];

		public void RentBufferFromPool(int sizeHint)
		{
			rentBuffer.EnsureCapacity(sizeHint);
		}

		/// <summary>
		/// 推进指针
		/// </summary>
		/// <param name="count"></param>
		public void Advance(uint count)
		{
			// 不会有2GiB大的包吧？
			Debug.Assert(count < int.MaxValue);
			Length += (int)count;
		}

		public readonly OperationStatus Encode(ref Span<byte> span, out int encodedLength)
		{
			if (span.Length < IKCP_OVERHEAD)
			{
				encodedLength = 0;
				return OperationStatus.DestinationTooSmall;
			}

			Header.Write(ref span);

			span = span[IKCP_OVERHEAD..];

			if (span.Length < Memory.Length)
			{
				encodedLength = IKCP_OVERHEAD;
				return OperationStatus.DestinationTooSmall;
			}

			Memory.Span.CopyTo(span[..Length]);
			encodedLength = Length + IKCP_OVERHEAD;

			span = span[Length..];

			return OperationStatus.Done;
		}

		public void Dispose()
		{
			rentBuffer.Dispose();
		}
	}

}
