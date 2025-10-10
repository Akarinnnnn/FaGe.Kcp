using FaGe.Kcp.Utility;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using static FaGe.Kcp.KcpConst;

namespace FaGe.Kcp.Connections;

public abstract class KcpConnectionBase : IDisposable
{
	/// <summary>
	/// 控制信号，输出用临时缓冲区
	/// </summary>
	protected static readonly ArrayPool<byte> OutputTemporaryBufferPool = ArrayPool<byte>.Create(
			(IKCP_MTU_DEF + IKCP_OVERHEAD) * 3,
			20 /* 脑测值 */);
#pragma warning disable IDE1006
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
	/// <summary>
	/// 当前计时tick值
	/// </summary>
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
	/// <summary>
	/// 取消拥塞控制
	/// </summary>
	protected bool NoCwnd => nocwnd != 0;
	// 考虑用EventSource
	protected int logmask;


	/// <summary>
	/// 发送 ack 队列 
	/// </summary>
	protected ConcurrentQueue<(uint sn, uint ts)> acklist = new();
	private readonly Queue<PacketAndBuffer> snd_queue;
	private readonly List<PacketAndBuffer> snd_buf;
	private readonly List<PacketAndBuffer> rcv_queue;
	private readonly List<PacketAndBuffer> rcv_buf;

	private RentBuffer buffer;

	private bool disposedValue;

	[Obsolete]
	private int stream => IsUsingStreamTransmission ? 1 : 0;

	private int ThreeAckPacketBufferSize => (int)(3 * (MTU + IKCP_OVERHEAD));

#pragma warning restore

	private readonly Pipe sendPipe;
	private readonly Pipe recvPipe;

	public PipeReader ReceiveReader => recvPipe.Reader;
	public PipeWriter SendWriter => sendPipe.Writer;

	protected KcpConnectionBase(bool isStreamTransimssion,
		PipeOptions? sendPipeOptions = default,
		PipeOptions? receivePipeOptions = default)
	{
		snd_wnd = IKCP_WND_SND;
		rcv_wnd = IKCP_WND_RCV;
		rmt_wnd = IKCP_WND_RCV;
		MTU = IKCP_MTU_DEF;

		// snd_queue = Channel.CreateUnbounded<PacketBuffer>(sendQueueOptions);
		// rcv_queue = Channel.CreateUnbounded<PacketBuffer>(receiveQueueOptions);
		snd_buf = new();
		rcv_queue = new();

		buffer = new(ThreeAckPacketBufferSize, OutputTemporaryBufferPool);
	}

	public event Action? OnDeadConnection = null;

	protected bool IsDisposed { get; private set; }

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
			buffer = new(ThreeAckPacketBufferSize, OutputTemporaryBufferPool);
		}
	}

	/// <summary>
	/// 是否采用流传输模式
	/// </summary>
	public bool IsUsingStreamTransmission { get; }

	public bool IsInNoDelayMode
	{
		get => nodelay != 0;
		set
		{
			if(value)
				nodelay = 1;
			else
				nodelay = 0;
		}
	}
	public KcpConnectionState State { get; private set; }

	private protected abstract ValueTask InvokeOutputCallbackAsync(ReadOnlySequence<byte> buffer, CancellationToken cancellationToken);

	public ValueTask UpdateAsync(uint timeTickNow, CancellationToken cancellationToken)
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
			ts_flush = current;
			slap = 0;
		}

		if (slap >= 0)
		{
			ts_flush += interval;
			if (TimeDiffSigned(current, ts_flush) >= 0)
			{
				ts_flush = current + interval;
			}
			// 魔改部分：调用异步刷新
			return FlushAsync(cancellationToken);
		}
		else
		{
			return ValueTask.CompletedTask;
		}
	}

	private ushort UnusedWindow
	{
		get
		{
			int waitCount = rcv_queue.Count;

			if (waitCount < rcv_wnd)
			{
				var count = rcv_wnd - waitCount;
				return (ushort)Math.Min(count, ushort.MaxValue);
			}

			return 0;
		}
	}

	/// <summary>
	/// 异步执行IO操作，通常在更新时钟之后执行。
	/// </summary>
	public async ValueTask FlushAsync(CancellationToken ct)
	{
		// 我们很难在这里使用局部变量来创建临时缓冲区，因为源自stackalloc的内存必须在堆栈上分配，无法在异步点跨越堆栈边界。
		// 因此，我们使用一个租赁的缓冲区来存储数据，直到调用输出回调。
		// `current`, store
		uint tickNow = current;

		int change = 0;
		int lost = 0;

		if (!updated)
			return;

		var genericHeader = KcpPacketHeader.FromMachine(new()
		{
			conv = conv,
			cmd = KcpCommand.Ack,
			frg = 0,
			wnd = UnusedWindow,
			una = rcv_nxt,
			len = 0,
			sn = 0,
			ts = 0
		});

		// 发送ACK包
		// 估测将使用3个包大小的buffer
		buffer.EnsureCapacity(ThreeAckPacketBufferSize);

		Memory<byte> genericEncodingBuffer = buffer.Memory;
		Memory<byte> currentEncodingBuffer = genericEncodingBuffer;

		while (acklist.TryDequeue(out var ack))
		{
			// 检查是否有足够空间写入ACK包
			// 记一下，这是计算已用空间的方式，等会儿要用。原因是忘了返回已编码长度了
			int size = genericEncodingBuffer.Length - currentEncodingBuffer.Length;
			if (size + IKCP_OVERHEAD > MTU)
			{
				// 已编码的ACK数量超过临时buffer容量，调用输出回调
				ReadOnlyMemory<byte> encodedBuffer = currentEncodingBuffer[..size];
				await InvokeOutputCallbackAsync(new(encodedBuffer), ct);
				currentEncodingBuffer = genericEncodingBuffer;
			}

			var ackHeader = KcpPacketHeader.FromMachine(genericHeader.ValueAnyEndian with { sn = ack.sn, ts = ack.ts });

			var encodeSpan = currentEncodingBuffer.Span;
			ackHeader.Write(ref encodeSpan);
			currentEncodingBuffer = currentEncodingBuffer[(currentEncodingBuffer.Length - encodeSpan.Length)..];
		}

		// 如果远端窗口大小为0，则探测接收窗口
		if (rmt_wnd == 0)
		{
			if (probe_wait == 0)
			{
				probe_wait = IKCP_PROBE_INIT;
				ts_probe = current + probe_wait;
			}
			else
			{
				if (TimeDiffSigned(current, ts_probe) >= 0)
				{
					if (probe_wait < IKCP_PROBE_INIT)
					{
						probe_wait = IKCP_PROBE_INIT;
					}

					probe_wait += probe_wait / 2;
					
					if (probe_wait > IKCP_PROBE_LIMIT)
						probe_wait = IKCP_PROBE_LIMIT;
					ts_probe = current + probe_wait;
					probe |= AskType.Send;

				}
			}
		}
		else
		{
			ts_probe = 0;
			probe_wait = 0;
		}

		// 发送探测包（探测远端窗口大小）
		if ((probe & AskType.Send) == AskType.Send)
		{
			int size = genericEncodingBuffer.Length - currentEncodingBuffer.Length;
			if (size + IKCP_OVERHEAD > MTU)
			{
				// 已编码的指令数量超过临时buffer容量，调用输出回调
				ReadOnlyMemory<byte> encodedBuffer = currentEncodingBuffer[..size];
				await InvokeOutputCallbackAsync(new(encodedBuffer), ct);
				currentEncodingBuffer = genericEncodingBuffer;
			}
			var probeHeader = KcpPacketHeader.FromMachine(genericHeader.ValueAnyEndian with { cmd = KcpCommand.WindowProbe });
			var encodeSpan = currentEncodingBuffer.Span;
			probeHeader.Write(ref encodeSpan);
		}

		// 发送探测包（通知远端我们的窗口大小）
		if ((probe & AskType.Tell) == AskType.Tell)
		{
			int size = genericEncodingBuffer.Length - currentEncodingBuffer.Length;
			if (size + IKCP_OVERHEAD > MTU)
			{
				// 已编码的指令数量超过临时buffer容量，调用输出回调
				ReadOnlyMemory<byte> encodedBuffer = currentEncodingBuffer[..size];
				await InvokeOutputCallbackAsync(new(encodedBuffer), ct);
				currentEncodingBuffer = genericEncodingBuffer;
			}
			var probeHeader = KcpPacketHeader.FromMachine(genericHeader.ValueAnyEndian with { cmd = KcpCommand.WindowSizeTell });
			var encodeSpan = currentEncodingBuffer.Span;
			probeHeader.Write(ref encodeSpan);
		}

		probe = AskType.None;

		uint cwndLocal = Math.Min(snd_wnd, rmt_wnd);
		if (NoCwnd)
		{
			cwndLocal = Math.Min(cwnd, cwndLocal);
		}

		// 将数据包从发送队列移动到发送缓冲区
		// 这里没有涉及IO操作，因此可以同步执行
		while (TimeDiffSigned(snd_nxt, snd_una + cwndLocal) < 0)
		{
			if (snd_queue.TryDequeue(out var packet))
			{
				var pushHeader = KcpPacketHeader.FromMachine(genericHeader.ValueAnyEndian with
				{
					cmd = KcpCommand.Push,
					// wnd 已经在genericHeader中设置
					ts = tickNow,
					sn = snd_nxt,
					una = rcv_nxt,

				});

				packet.Header = pushHeader;

				packet.resendts = tickNow;
				packet.rto = rx_rto;
				packet.fastack = 0;
				packet.xmit = 0;

				snd_buf.Add(packet);
				snd_nxt++;

			}
			else
			{
				break;
			}
		}

		var resent = fastresend > 0 ? (uint)fastresend : 0xffffffff;
		var rtomin = IsInNoDelayMode ? (rx_rto >> 3) : 0;

		// flush data segments
		foreach (var item in snd_buf)
		{
			var segment = item;
			var needsend = false;
			
			if (segment.xmit == 0)
			{
				//新加入 snd_buf 中, 从未发送过的报文直接发送出去;
				needsend = true;
				segment.xmit++;
				segment.rto = rx_rto;
				segment.resendts = tickNow + rx_rto + rtomin;
			}
			else if (TimeDiffSigned(tickNow, segment.resendts) >= 0)
			{
				//发送过的, 但是在 RTO 内未收到 ACK 的报文, 需要重传;
				needsend = true;
				segment.xmit++;
				xmit++;
				if (IsInNoDelayMode)
				{
					segment.rto += Math.Max(segment.rto, rx_rto);
				}
				else
				{
					var step = nodelay < 2 ? segment.rto : rx_rto;
					segment.rto += step / 2;
				}

				segment.resendts = tickNow + segment.rto;
				lost = 1;
			}
			else if (segment.fastack >= resent)
			{
				//发送过的, 但是 ACK 失序若干次的报文, 需要执行快速重传.
				if (segment.xmit <= fastlimit
					|| fastlimit <= 0)
				{
					needsend = true;
					segment.xmit++;
					segment.fastack = 0;
					segment.resendts = tickNow + segment.rto;
					change++;
				}
			}

			if (needsend)
			{
				segment.Header = KcpPacketHeader.FromMachine(segment.Header.ValueAnyEndian with
				{
					ts = tickNow,
					wnd = genericHeader.ValueAnyEndian.wnd, // 特殊方式获取方法开始时的wnd值
					una = rcv_nxt,
				});

				var need = IKCP_OVERHEAD + segment.Length;
				// using RentBuffer buffer = new((int)MTU + IKCP_OVERHEAD, ArrayPool<byte>.Shared);
				int size = genericEncodingBuffer.Length - currentEncodingBuffer.Length;
				if ( + need > MTU)
				{
					await InvokeOutputCallbackAsync(new(genericEncodingBuffer[..size]), ct);
					currentEncodingBuffer = genericEncodingBuffer;
				}

				// offset += segment.Encode(buffer.Memory.Span.Slice(offset));
				var span = currentEncodingBuffer.Span;
				if (segment.Encode(ref span, out int encodedLength) == OperationStatus.Done)
				{
					currentEncodingBuffer = currentEncodingBuffer[encodedLength..];
				}
				else
				{
					// Log
				}

				// 用EventSource改写
				//if (CanLog(KcpLogMask.IKCP_LOG_NEED_SEND))
				//{
				//	LogWriteLine($"{segment.ToLogString(true)}", KcpLogMask.IKCP_LOG_NEED_SEND.ToString());
				//}

				if (segment.xmit >= dead_link)
				{
					state = -1;

					// 用EventSource改写
					//if (CanLog(KcpLogMask.IKCP_LOG_DEAD_LINK))
					//{
					//	LogWriteLine($"state = -1; xmit:{segment.xmit} >= dead_link:{dead_link}", KcpLogMask.IKCP_LOG_DEAD_LINK.ToString());
					//}
				}
			}

			int remainingBufferLen = genericEncodingBuffer.Length - currentEncodingBuffer.Length;
			if (remainingBufferLen > 0)
			{
				// 已编码的指令数量超过临时buffer容量，调用输出回调
				ReadOnlyMemory<byte> encodedBuffer = currentEncodingBuffer[..remainingBufferLen];
				await InvokeOutputCallbackAsync(new(encodedBuffer), ct);
				currentEncodingBuffer = genericEncodingBuffer;
			}

			#region update ssthresh
			// update ssthresh 根据丢包情况计算 ssthresh 和 cwnd.
			if (change != 0)
			{
				var inflight = snd_nxt - snd_una;
				ssthresh = inflight / 2;
				if (ssthresh < IKCP_THRESH_MIN)
				{
					ssthresh = IKCP_THRESH_MIN;
				}

				cwnd = ssthresh + resent;
				incr = cwnd * mss;
			}

			if (lost != 0)
			{
				ssthresh = cwnd / 2;
				if (ssthresh < IKCP_THRESH_MIN)
				{
					ssthresh = IKCP_THRESH_MIN;
				}

				cwnd = 1;
				incr = mss;
			}

			if (cwnd < 1)
			{
				cwnd = 1;
				incr = mss;
			}
			#endregion

			if (state == -1)
			{
				OnDeadConnection?.Invoke();
			}
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
		// TODO
		throw new NotImplementedException();
	}


	private sealed unsafe class StackBufferOwner(int length, byte* stackBuffer) : MemoryManager<byte>
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

	private void DragElement<T>(List<T> list, T element, int destniation)
		where T : IEquatable<T>
	{
		int currentIndex = -1;
		for (int i = 0; i < list.Count; i++)
		{
			if (list[i].Equals(element))
			{
				currentIndex = i;
			}
		}

		if (currentIndex == -1)
		{
			throw new KeyNotFoundException("集合中找不到所查找的元素");
		}

		list.RemoveAt(currentIndex);
		list.Insert(destniation, element);
	}

	protected virtual void Dispose(bool disposing)
	{
		if (!disposedValue)
		{
			if (disposing)
			{
				// TODO: 释放托管状态(托管对象)
			}

			// TODO: 释放未托管的资源(未托管的对象)并重写终结器
			// TODO: 将大型字段设置为 null
			disposedValue = true;
		}
	}

	// // TODO: 仅当“Dispose(bool disposing)”拥有用于释放未托管资源的代码时才替代终结器
	// ~KcpConnectionBase()
	// {
	//     // 不要更改此代码。请将清理代码放入“Dispose(bool disposing)”方法中
	//     Dispose(disposing: false);
	// }

	public void Dispose()
	{
		// 不要更改此代码。请将清理代码放入“Dispose(bool disposing)”方法中
		Dispose(disposing: true);
		GC.SuppressFinalize(this);
	}
}
