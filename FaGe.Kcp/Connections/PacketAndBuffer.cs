using FaGe.Kcp.Utility;
using System.Buffers;
using System.Diagnostics;
using static FaGe.Kcp.KcpConst;

namespace FaGe.Kcp.Connections
{
	// 我在想struct是不是更好一些，或者class方便修改和传递引用
	internal class PacketAndBuffer(KcpPacketHeader header, ArrayPool<byte> bufferSource)
		: IDisposable
	{
		private RentBuffer rentBuffer = new(0, bufferSource);

		public Memory<byte> RentBuffer => rentBuffer.Memory;

		public KcpPacketHeader Header = header; // 不readonly，因为构造后可能需要修改

		/// <summary>
		/// 开始写入数据的偏移量
		/// </summary>
		public int WritingBeginOffset;
		/// <summary>
		/// 写入的数据长度
		/// </summary>
		public int Length;

		// 这些也是会变的，在FlushAsync里会更新
		public uint resendts;
		public uint rto;
		public uint fastack;
		public uint xmit;

		public PacketAndBuffer(KcpPacketHeader header) : this(header, ArrayPool<byte>.Shared)
		{
		}

		public Memory<byte> EncodedMemory => RentBuffer.Slice(IKCP_OVERHEAD, Length);
		public Memory<byte> RemainingMemory => RentBuffer[(WritingBeginOffset + IKCP_OVERHEAD)..];

		public int Capacity => RentBuffer.Length - IKCP_OVERHEAD;

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
			Debug.Assert(EncodedMemory.Length <= Length + count);
			Advance(count);
		}

		/// <summary>
		/// 推进指针
		/// </summary>
		/// <param name="count"></param>
		public void Advance(int count)
		{
			Debug.Assert(EncodedMemory.Length <= Length + count);

			Length += count;
			WritingBeginOffset += count;
		}

		public OperationStatus Encode(ref Span<byte> span, out int encodedLength)
		{
			if (span.Length < IKCP_OVERHEAD + Length)
			{
				encodedLength = 0;
				return OperationStatus.DestinationTooSmall;
			}

			Header.Write(ref span);

			span = span[IKCP_OVERHEAD..];

			if (span.Length < Length)
			{
				encodedLength = IKCP_OVERHEAD;
				return OperationStatus.DestinationTooSmall;
			}

			EncodedMemory.Span.CopyTo(span[..Length]);
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
