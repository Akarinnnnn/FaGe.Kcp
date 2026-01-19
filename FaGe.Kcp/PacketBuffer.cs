using FaGe.Kcp.Utility;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static FaGe.Kcp.KcpConst;

namespace FaGe.Kcp
{
	// 用class，需要引用语义。原因是保持所有同源buffer的一致性，避免struct拷贝带来的不一致
	internal class PacketBuffer(ArrayPool<byte> bufferSource, int expectedCapacity, bool isMachineEndian)
				: IDisposable
	{
		private RentBuffer rentBuffer = new(expectedCapacity + IKCP_OVERHEAD, bufferSource);
		
		internal TaskCompletionSource? AsyncState { get; private set; }

		internal CancellationTokenSource? DisposeCts { get; private set; }

		internal void SetOnPacketFinished(TaskCompletionSource tcs, CancellationToken ct)
		{
			AsyncState = tcs;
			DisposeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			DisposeCts.Token.Register(() =>
			{
				AsyncState.TrySetCanceled(DisposeCts.Token);
				Dispose();
			});
		}

		internal void MarkPacketCompleted()
		{
			DisposeCts?.Dispose();

			DisposeCts = null;
			AsyncState = null;
		}

		public bool IsMachineEndian { get; private set; } = isMachineEndian;

		// 不返回ref readonly，因为构造后可能需要修改
		internal ref KcpPacketHeaderAnyEndian HeaderRef =>
				ref Unsafe.As<byte, KcpPacketHeaderAnyEndian>(ref HeaderMemory.Span.GetPinnableReference());

		public KcpPacketHeaderAnyEndian HeaderAnyEndian => HeaderRef;

		/// <summary>
		/// 
		/// </summary>
		public KcpPacketHeader Header => new(HeaderRef, IsMachineEndian);

		public PacketBuffer(ArrayPool<byte> bufferSource, KcpPacketHeader header = default, int expectedCapacity = 0)
			: this(bufferSource, expectedCapacity, header.IsMachineEndian)
		{
			HeaderRef = header.ValueAnyEndian;
			IsMachineEndian = header.IsMachineEndian;
		}

		/// <summary>
		/// 开始写入数据的偏移量
		/// </summary>
		public int WriteBeginningOffset => Length + IKCP_OVERHEAD;
		/// <summary>
		/// 写入的数据长度
		/// </summary>
		public int Length { get; private set; }

		private Memory<byte> RentBuffer => rentBuffer.Memory;

		/// <summary>
		/// 控制字
		/// </summary>
		public PacketControlFields PacketControlFields = default;

		/// <summary>
		/// KCP包头内容
		/// </summary>
		public Memory<byte> HeaderMemory => RentBuffer[..IKCP_OVERHEAD];

		/// <summary>
		/// 包中payload内容
		/// </summary>
		public Memory<byte> PayloadMemory => RentBuffer.Slice(IKCP_OVERHEAD, Length);

		/// <summary>
		/// Buffer剩余空间视图
		/// </summary>
		public Memory<byte> RemainingMemory => RentBuffer[WriteBeginningOffset..];

		/// <summary>
		/// 全包视图
		/// </summary>
		public Memory<byte> PacketMemory => RentBuffer[..(IKCP_OVERHEAD + Length)];

		public int Capacity => RentBuffer.Length - IKCP_OVERHEAD;

		public void ConvertHeaderToMachineEndian()
		{
			if (!IsMachineEndian && !BitConverter.IsLittleEndian) // 改大端要改这里
			{
				HeaderRef = HeaderRef.ReverseEndianness();
				IsMachineEndian = true;
			}
		}

		public void ConvertHeaderToNetworkEndian()
		{
			if (IsMachineEndian && !BitConverter.IsLittleEndian) // 改大端要改这里
			{
				HeaderRef = HeaderRef.ReverseEndianness();
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
			Debug.Assert(PayloadMemory.Length <= Length + count);
			Advance((int)count);
		}

		/// <summary>
		/// 推进指针
		/// </summary>
		/// <param name="count"></param>
		public void Advance(int count)
		{
			Debug.Assert(PayloadMemory.Length <= Length + count);

			Length += count;
		}

		public OperationStatus Encode(ref Span<byte> span, out int encodedLength)
		{
			if (span.Length < IKCP_OVERHEAD + Length)
			{
				encodedLength = 0;
				return OperationStatus.DestinationTooSmall;
			}

			HeaderRef.len = (uint)Length;

			ConvertHeaderToNetworkEndian();

			PacketMemory.Span.CopyTo(span[..(Length + IKCP_OVERHEAD)]);
			encodedLength = Length + IKCP_OVERHEAD;

			span = span[(Length + IKCP_OVERHEAD)..];

			return OperationStatus.Done;
		}

		public static PacketBuffer FromNetwork(ReadOnlyMemory<byte> packetBuffer, ArrayPool<byte> bufferSource)
		{
			PacketBuffer result = new(bufferSource, packetBuffer.Length, false);

			if (result.rentBuffer.Buffer!.Length < packetBuffer.Length)
				result.rentBuffer.EnsureCapacity(packetBuffer.Length);

			packetBuffer.CopyTo(result.RentBuffer);
			result.ConvertHeaderToMachineEndian();
			
			result.Length = (int)result.HeaderRef.len;

#if DEBUG
			Debug.Assert(result.Length + IKCP_OVERHEAD == packetBuffer.Length, "Packet's lentgh is mismatch between machine and network representation.");
#endif

			return result;
		}

		public void Dispose()
		{
			DisposeCts?.Cancel();
			rentBuffer.Dispose();
		}
	}

}