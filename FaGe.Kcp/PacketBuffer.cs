using FaGe.Kcp.Utility;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static FaGe.Kcp.KcpConst;

namespace FaGe.Kcp
{
	// 用class，需要引用语义。原因是保持所有同源buffer的一致性，避免struct拷贝带来的不一致
	internal class PacketBuffer(ArrayPool<byte> bufferSource, int expectedCapacity)
				: IDisposable
	{
		private RentBuffer rentBuffer = new(expectedCapacity + IKCP_OVERHEAD, bufferSource);
		public bool IsMachineEndian { get; private set; }


		// 不返回ref readonly，因为构造后可能需要修改
		public ref KcpPacketHeaderAnyEndian HeaderAnyEndian =>
				ref Unsafe.As<byte, KcpPacketHeaderAnyEndian>(ref HeaderMemory.Span.GetPinnableReference()); 

		public KcpPacketHeader Header => new(HeaderAnyEndian, IsMachineEndian);

		public PacketBuffer(ArrayPool<byte> bufferSource, KcpPacketHeader header = default, int expectedCapacity = 0)
			: this(bufferSource, expectedCapacity)
		{
			HeaderAnyEndian = header.ValueAnyEndian;
			IsMachineEndian = header.IsMachineEndian;
		}

		/// <summary>
		/// 开始写入数据的偏移量
		/// </summary>
		public int WritingBeginOffset;
		/// <summary>
		/// 写入的数据长度
		/// </summary>
		public int Length { get; private set; }

		private Memory<byte> RentBuffer => rentBuffer.Memory;


		public PacketControlFields PacketControlFields = default;

		public Memory<byte> HeaderMemory => RentBuffer[..IKCP_OVERHEAD];

		public Memory<byte> EncodedMemory => RentBuffer.Slice(IKCP_OVERHEAD, Length);

		public Memory<byte> RemainingMemory => RentBuffer[(WritingBeginOffset + IKCP_OVERHEAD)..];

		public Memory<byte> PacketMemory => RentBuffer[..(IKCP_OVERHEAD + Length)];

		public int Capacity => RentBuffer.Length - IKCP_OVERHEAD;

		public void ConvertHeaderToMachineEndian()
		{
			if (!IsMachineEndian)
			{
				HeaderAnyEndian = HeaderAnyEndian.ReverseEndianness();
				IsMachineEndian = true;
			}
		}

		public void ConvertHeaderToNetworkEndian()
		{
			if (IsMachineEndian)
			{
				HeaderAnyEndian = HeaderAnyEndian.ReverseEndianness();
				IsMachineEndian = false;
			}
		}

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

			ConvertHeaderToMachineEndian();

			PacketMemory.Span.CopyTo(span[..Length]);
			encodedLength = Length + IKCP_OVERHEAD;

			span = span[(Length + IKCP_OVERHEAD)..];

			return OperationStatus.Done;
		}

		public void Dispose()
		{
			rentBuffer.Dispose();
		}
	}

}
