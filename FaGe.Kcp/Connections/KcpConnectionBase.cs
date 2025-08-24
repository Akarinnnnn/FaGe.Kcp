using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using static FaGe.Kcp.KcpConst;

namespace FaGe.Kcp.Connections;

public abstract class KcpConnectionBase
{
	/// <summary>
	/// 控制信号，输出用临时缓冲区
	/// </summary>
	protected static readonly ArrayPool<byte> OutputTemporaryBufferPool = ArrayPool<byte>.Create(IKCP_MTU_DEF, 40 /* 脑测值 */);

	/// <summary>
	/// 频道号
	/// </summary>
	protected uint conv { get; private set; }
	/// <summary>
	/// 最大传输单元（Maximum Transmission Unit，MTU）
	/// </summary>
	protected uint mtu;

	/// <summary>
	/// 最大报文段长度
	/// </summary>
	protected uint mss;
	/// <summary>
	/// 连接状态（0xFFFFFFFF表示断开连接）
	/// </summary>
	protected int state;
	/// <summary>
	/// 第一个未确认的包
	/// </summary>
	protected uint snd_una;
	/// <summary>
	/// 待发送包的序号
	/// </summary>
	protected uint snd_nxt;
	/// <summary>
	/// 下一个等待接收消息ID,待接收消息序号
	/// </summary>
	protected uint rcv_nxt;
	protected uint ts_recent;
	protected uint ts_lastack;
	/// <summary>
	/// 拥塞窗口阈值
	/// </summary>
	protected uint ssthresh;
	/// <summary>
	/// ack接收rtt浮动值
	/// </summary>
	protected uint rx_rttval;
	/// <summary>
	/// ack接收rtt静态值
	/// </summary>
	protected uint rx_srtt;
	/// <summary>
	/// 由ack接收延迟计算出来的复原时间。Retransmission TimeOut(RTO), 超时重传时间.
	/// </summary>
	protected uint rx_rto;
	/// <summary>
	/// 最小复原时间
	/// </summary>
	protected uint rx_minrto;
	/// <summary>
	/// 发送窗口大小
	/// </summary>
	protected uint snd_wnd;
	/// <summary>
	/// 接收窗口大小
	/// </summary>
	protected uint rcv_wnd;
	/// <summary>
	/// 远端接收窗口大小
	/// </summary>
	protected uint rmt_wnd;
	/// <summary>
	/// 拥塞窗口大小
	/// </summary>
	protected uint cwnd;
	/// <summary>
	/// 探查变量，IKCP_ASK_TELL表示告知远端窗口大小。IKCP_ASK_SEND表示请求远端告知窗口大小
	/// </summary>
	protected AskType probe;
	protected uint current;
	/// <summary>
	/// 内部flush刷新间隔
	/// </summary>
	protected uint interval;
	/// <summary>
	/// 下次flush刷新时间戳
	/// </summary>
	protected uint ts_flush;
	protected uint xmit;
	/// <summary>
	/// 是否启动无延迟模式
	/// </summary>
	protected uint nodelay;
	/// <summary>
	/// 是否调用过update函数的标识
	/// </summary>
	protected bool updated;
	/// <summary>
	/// 下次探查窗口的时间戳
	/// </summary>
	protected uint ts_probe;
	/// <summary>
	/// 探查窗口需要等待的时间
	/// </summary>
	protected uint probe_wait;
	/// <summary>
	/// 最大重传次数
	/// </summary>
	protected uint dead_link;
	/// <summary>
	/// 可发送的最大数据量
	/// </summary>
	protected uint incr;
	/// <summary>
	/// 触发快速重传的重复ack个数
	/// </summary>
	public int fastresend;
	public int fastlimit;
	/// <summary>
	/// 取消拥塞控制
	/// </summary>
	protected int nocwnd;
	// 考虑用EventSource
	protected int logmask;


	/// <summary>
	/// 发送 ack 队列 
	/// </summary>
	protected ConcurrentQueue<(uint sn, uint ts)> acklist = new ConcurrentQueue<(uint sn, uint ts)>();
	private readonly PacketSequence snd_queue;
	private readonly PacketSequence rcv_queue;
	private readonly ConcurrentQueue<PacketBuffer> snd_buf;
	private readonly LinkedList<PacketBuffer> rcv_buf;

	[Obsolete]
	private int stream => IsUsingStreamTransmission ? 1 : 0;

	protected KcpConnectionBase(
		UnboundedChannelOptions sendQueueOptions,
		UnboundedChannelOptions receiveQueueOptions
	) {
		snd_wnd = IKCP_WND_SND;
		rcv_wnd = IKCP_WND_RCV;
		rmt_wnd = IKCP_WND_RCV;
		MTU = IKCP_MTU_DEF;

		// snd_queue = Channel.CreateUnbounded<PacketBuffer>(sendQueueOptions);
		// rcv_queue = Channel.CreateUnbounded<PacketBuffer>(receiveQueueOptions);
		snd_buf = new();
		rcv_buf = new();
	}



	private protected bool IsDisposed { get; private set; }

	public uint MTU
	{
		get => mtu;
		set
		{
			var newmtu = value;
			var newmss = value - IKCP_OVERHEAD;

			if (newmtu > int.MaxValue)
				throw new ArgumentOutOfRangeException(nameof(value));

			mtu = newmtu;
			mss = newmss;
		}
	}

	/// <summary>
	/// 是否采用流传输模式
	/// </summary>
	public bool IsUsingStreamTransmission { get; set; }

	public KcpConnectionState State { get; private set; }

	private protected abstract ValueTask InvokeOutputCallbackAsync(ReadOnlySequence<byte> buffer, CancellationToken cancellationToken);

	private protected void BaseUpdate(uint timeTickNow)
	{
		current = timeTickNow;
		
		if (!updated)
		{
			updated = true;
			ts_flush = current;
		}

		int slap = TimeDiffSigned(timeTickNow, ts_flush);

		// +-10s
		if (slap >= 10000 || slap < -10000)
		{
			ts_flush += interval;
			if (TimeDiffSigned(current, ts_flush) >= 0)
			{
				ts_flush = current + interval;
			}

			// 输出在PerformIOAsync，更新不调用任何异步输出
		}
	}

	private ushort UnusedWindow()
	{
		// TODO
		throw new NotImplementedException();
	}

	private void PopulateAckHeader((uint sn, uint ts) ackDesc, ref KcpPacketHeaderAnyEndian machineEndianHeader)
	{
		machineEndianHeader.sn = ackDesc.sn;
		machineEndianHeader.ts = ackDesc.ts;
	}

	/// <summary>
	/// 异步执行IO操作，通常在更新时钟之后执行。
	/// </summary>
	public async ValueTask PerformIOAsync(CancellationToken ct)
	{
		// `current`, store
		uint tickNow = current;
		// 魔改部分：用 pipe 代替字节数组
		// Pipe controlTempBufferLocal = controlTempBuffer;
		// PipeReader tempBuffReader = controlTempBufferLocal.Reader;
		// PipeWriter tempBuffWriter = controlTempBufferLocal.Writer;

		// Memory<byte> ptr = bufferLocal;
		int change = 0;
		int lost = 0;
		int offset = 0;

		if (!updated)
			return;

		ushort wnd = UnusedWindow();

		// 发送ACK包
		try
		{
			//byte* ackBufferUnderlying = stackalloc byte[PacketBufferLength]; // 无法在不安全的上下文中等待
			//StackBufferOwner ackBuffer = new(PacketBufferLength, ackBufferUnderlying);
			// 复用PacketBuffer用于编码ACK包流

			// 估测将使用3个包大小的buffer
			PacketBuffer packet = new(tempBuffWriter.GetMemory((int)(IKCP_OVERHEAD + MTU) * 3), new(
				new() {
					conv = conv,
					cmd = KcpCommand.Ack,
					wnd = wnd,
					una = rcv_nxt
				},
			false));

			while (acklist.TryDequeue(out var ack))
			{
				buffer.

				ReadResult encodeResult = ;
				ReadOnlySequence<byte> encodeResultBuffer = encodeResult.Buffer;
				long size = encodeResultBuffer.Length;

				if (size + KcpPacketHeaderAnyEndian.ExpectedSize > MTU)
				{
					// 已编码的ACK数量超过临时buffer容量，准备调用输出回调
					await InvokeOutputCallbackAsync(encodeResultBuffer, ct);
				}


			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(ackTempBuffer);
		}

	}

	protected static int TimeDiffSigned(uint tickLater, uint tickEarlier)
	{
		return (int)(tickLater - tickEarlier);
	}

	private static void EncodeGeneric<T>(T value, ref Span<byte> dstSpan) where T : unmanaged
	{
		int byteSize = Unsafe.SizeOf<T>();
		Unsafe.CopyBlockUnaligned(ref dstSpan.GetPinnableReference(), ref Unsafe.As<T, byte>(ref value), (uint)byteSize);
		dstSpan = dstSpan[byteSize..];
	}

	internal KcpPacketHeader PrepareSendingPacketHeader(int packetLength)
	{
		throw new NotImplementedException();
	}

	private struct PacketBuffer(KcpPacketHeader header)
	{
		/// <summary>
		/// Segment No.
		/// </summary>
		public uint sn;
		public readonly Memory<byte> RentBuffer;
		public KcpPacketHeader Header = header;
		public int BeginOffset;
		public int Length;

		public PacketBuffer(Memory<byte> buffer, KcpPacketHeader header) : this(header)
		{
			RentBuffer = buffer;
		}

		public readonly Memory<byte> Memory => RentBuffer.Slice(BeginOffset + IKCP_OVERHEAD, Length);
		public readonly Memory<byte> RemainingMemory => RentBuffer.Slice(BeginOffset + IKCP_OVERHEAD + Length);

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

		public readonly OperationStatus EncodeHeader(Span<byte> span)
		{
			if (span.Length < IKCP_OVERHEAD)
			{
				return OperationStatus.DestinationTooSmall;
			}

			var header = Header.ToTransportForm().ValueAnyEndian;

			EncodeGeneric(header.conv, ref span);
			EncodeGeneric(header.cmd, ref span);
			EncodeGeneric((byte)header.frag, ref span);
			EncodeGeneric(header.wnd, ref span);
			EncodeGeneric(header.ts, ref span);
			EncodeGeneric(header.sn, ref span);
			EncodeGeneric(header.una, ref span);
			EncodeGeneric(header.len, ref span);

			return OperationStatus.Done;
		}
	}

	private unsafe class StackBufferOwner(int length, byte* stackBuffer) : MemoryManager<byte>
	{
		private readonly byte* stackBuffer = stackBuffer;
		private readonly int length = length;

		public void Dispose()
		{
			throw new NotImplementedException();
		}

		public override Span<byte> GetSpan()
		{
			return new(stackBuffer, length);
		}

		public override MemoryHandle Pin(int elementIndex = 0)
		{
			return new(stackBuffer);
		}

		public override void Unpin()
		{
			// no-op, 栈内存无需解固定
		}

		protected override void Dispose(bool disposing)
		{
			// 此类不拥有资源，不进行释放
		}
	}
}
