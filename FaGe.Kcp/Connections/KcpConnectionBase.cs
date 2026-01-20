using DanmakuR.Protocol.Buffer;
using FaGe.Kcp.Utility;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
using static FaGe.Kcp.KcpConst;

namespace FaGe.Kcp.Connections;

/// <summary>
/// KCP 连接基类，封装了KCP协议的核心逻辑。该类型基本上并发不安全。
/// </summary>
/// <remarks>
/// 以下方法并发不安全，调用方必须保证以非并发的方式调用它们。<br/>
/// 包含<list type="bullet">
/// <item>
/// <description><see cref="Update(uint)"/>以及异步版本
/// </description>
/// </item>
/// <item><description><see cref="FlushAsync(CancellationToken)"/></description></item>
/// <item><description><see cref="QueueToSenderWithAsyncSource(ReadOnlyMemory{byte}, TaskCompletionSource?, CancellationToken)"/>以及受保护异步封装</description></item>
/// <item><description><see cref="InputFromUnderlyingTransport(ReadOnlyMemory{byte})"/></description></item>
/// <item><description><see cref="TryReadPacket(CancellationToken)"/>以及受保护异步封装</description></item>
/// </list>
/// </remarks>
public abstract class KcpConnectionBase : IDisposable
{
	/// <summary>
	/// 控制信号，输出用临时缓冲区
	/// </summary>
	protected static readonly ArrayPool<byte> AckOutputTemporaryBufferPool = ArrayPool<byte>.Create(
			(IKCP_MTU_DEF + IKCP_OVERHEAD) * 3,
			20 /* 脑测值 */);
#pragma warning disable IDE1006
	#region KCP内部基元类型状态变量
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
	///// <summary>
	///// 连接状态（0xFFFFFFFF表示断开连接）
	///// </summary>
	// protected int state;
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
	protected bool NoCwnd { get => nocwnd != 0; set => nocwnd = value ? 1 : 0; }
	// 考虑用EventSource
	protected int logmask;
	#endregion

	/// <summary>
	/// 发送 ack 队列 
	/// </summary>
	protected readonly ConcurrentQueue<(uint sn, uint ts)> acklist = new();
	private readonly Queue<PacketBuffer> snd_queue = new();
	private readonly LinkedList<PacketBuffer> snd_buf;
	private readonly Queue<PacketBuffer> rcv_queue;
	private readonly LinkedList<PacketBuffer> rcv_buf = new();

	// 源自source.dot.net, Pipe.cs的降分配技巧
	// 现在暂时用不上，哪天我想用了再说
	private readonly Stack<PacketBuffer> recycledBuffers = new();

	private PacketBuffer.FlushPacketBuffer flushBuffer;

	private bool disposedValue;

	private uint maxRcvWindow = IKCP_WND_RCV;
	private bool isWindowFull = false;

	[Obsolete]
	private int stream => IsStreamMode ? 1 : 0;

	private int ControlPacketBufferSize => (int)(mtu % IKCP_OVERHEAD * IKCP_OVERHEAD);

#pragma warning restore

	public int ConnectionId => (int)conv;
	public uint ConnectionIdUnsigned => conv;

	TaskCompletionSource? pendingReceiveTaskSource = null;

	protected KcpConnectionBase(
		uint conversationId,
		KcpConnectionOptionsBase? options = null)
	{
		conv = conversationId;
		snd_wnd = IKCP_WND_SND;
		rcv_wnd = IKCP_WND_RCV;
		rmt_wnd = IKCP_WND_RCV;
		MTU = IKCP_MTU_DEF;
		dead_link = IKCP_DEADLINK;

		snd_buf = new();
		rcv_queue = new();

		flushBuffer = new(AckOutputTemporaryBufferPool, ControlPacketBufferSize);
	}

	public event Action? OnConnectionWasClosed;

	protected void InvokeOnConnectionWasClosed()
		=> OnConnectionWasClosed?.Invoke();

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
			flushBuffer = new(AckOutputTemporaryBufferPool, ControlPacketBufferSize);
		}
	}

	public bool IsWindowFull => isWindowFull;  // 窗口是否已满（只读）

	public int MaxReceiveWindow                 // 最大接收窗口硬限制
	{
		get => (int)maxRcvWindow;
		set
		{
			ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, nameof(MaxReceiveWindow));
			ArgumentOutOfRangeException.ThrowIfGreaterThan(value, IKCP_WND_RCV, nameof(MaxReceiveWindow));

			maxRcvWindow = (uint)value;

			// 如果当前窗口大于新的最大值，自动调整
			if (rcv_wnd > maxRcvWindow)
				rcv_wnd = maxRcvWindow;
		}
	}

	public int MaxUserTransferLength => (int)mss;

	public int SendWindowSize
	{
		get => (int)snd_wnd;
		set
		{
			ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, nameof(SendWindowSize));
			snd_wnd = (uint)value;
		}
	}
	public int RecvWindowSize
	{
		get => (int)rcv_wnd;
		set
		{
			ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, nameof(RecvWindowSize));
			rcv_wnd = (uint)value;
		}
	}

	/// <summary>
	/// 是否采用流传输模式
	/// </summary>
	public bool IsStreamMode { get; set; }

	public bool IsNoDelayMode
	{
		get => nodelay != 0;
		private set
		{
			if (value)
				nodelay = 1;
			else
				nodelay = 0;
		}
	}

	public int PacketPendingSentCount => snd_buf.Count + snd_queue.Count;

	public uint ConversationId => conv;

	private ushort UnusedWindow
	{
		get
		{
			UpdateWindowFullState();

			if (IsWindowFull)
				return 0;

			int waitCount = rcv_queue.Count + rcv_buf.Count;
			uint effectiveWnd = Math.Min(rcv_wnd, maxRcvWindow);

			if (waitCount >= effectiveWnd)
				return 0;

			var count = rcv_wnd - waitCount;
			return (ushort)Math.Min(count, ushort.MaxValue);
		}
	}


	/// <summary>
	/// 调用输出回调以发送数据。
	/// </summary>
	/// <remarks>
	/// 重写的实现可以固定使用特定的传输机制（如UDP套接字）输出数据，而不是由外部提供输出方案。
	/// </remarks>
	/// <param name="buffer"></param>
	/// <param name="cancellationToken"></param>
	/// <returns></returns>
	protected abstract ValueTask InvokeOutputCallbackAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);

	/// <summary>
	/// 
	/// </summary>
	/// <param name="buffer"></param>
	/// <param name="source"></param>
	/// <param name="ct"></param>
	/// <returns></returns>
	public KcpSendResult QueueToSenderWithAsyncSource(ReadOnlyMemory<byte> buffer, TaskCompletionSource? source, CancellationToken ct)
	{
		ObjectDisposedException.ThrowIf(IsDisposed, this);
		PacketBuffer? packetOfLastFragment = null;
		int sent = 0;
		Debug.Assert(mss > 0);
		if (buffer.Length < 0)
			return KcpSendResult.Fail(KcpSendStatus.EmptyBuffer);

		// append to previous segment in streaming mode (if possible)
		if (IsStreamMode)
		{
			if (snd_queue.Count >= 1)
			{
				PacketBuffer lastPacket = snd_queue.Last();
				packetOfLastFragment = lastPacket;
				Debug.Assert(lastPacket.IsMachineEndian);
				if (lastPacket.Length < mss && lastPacket.Capacity >= mss)
				{
					if (!buffer.IsEmpty)
					{
						int remainingCapacity = lastPacket.Capacity - lastPacket.Length;
						if (remainingCapacity < mss)
							// 魔改：由于PacketAndBuffer和C实现的IKCPSEG不同，
							// 所以扩大容量不是解分配再重新分配，而是直接租赁更大的缓冲区
							lastPacket.RentBufferFromPool((int)mss);

						int copyLen = Math.Min(lastPacket.Capacity - lastPacket.Length,
							buffer.Length);
						buffer.Span.CopyTo(lastPacket.RemainingMemory.Span);

						buffer = buffer[copyLen..];

						sent += copyLen;
					}
				}
			}
		}

		if (buffer.IsEmpty)
		{
			if (source is not null)
			{
				packetOfLastFragment?.SetOnPacketFinished(source, ct);
				return KcpSendResult.Succeed(sent, source);
			}
			else
			{
				return KcpSendResult.Succeed(sent);
			}
		}

		int packetCount;

		if (buffer.Length <= mss)
			packetCount = 1;
		else
			packetCount = (int)(buffer.Length + mss - 1) / (int)mss;

		if (packetCount >= IKCP_WND_RCV)
		{
			if (IsStreamMode && sent > 0)
			{
				if (source is not null)
				{
					packetOfLastFragment?.SetOnPacketFinished(source, ct);
					return KcpSendResult.Succeed(sent, source);
				}
				else
				{
					return KcpSendResult.Succeed(sent);
				}
			}
			else
			{
				return KcpSendResult.Fail(KcpSendStatus.TryAgainLater);
			}
		}

		// 标准化packetCount，防止为0
		packetCount = packetCount == 0 ? 1 : packetCount;

		for (int i = 0; i < packetCount; i++)
		{
			if (ct.IsCancellationRequested)
			{
				if (source is not null)
				{
					source.SetCanceled(ct);
					return KcpSendResult.Succeed(sent, source);
				}
				else
				{
					return KcpSendResult.Succeed(sent);
				}
			}

			int size = buffer.Length > (int)mss ? (int)mss : buffer.Length;
			PacketBuffer newPacket = new(ArrayPool<byte>.Shared, size, true);

			buffer.Span[..size].CopyTo(newPacket.RemainingMemory.Span);
			newPacket.Advance(size);
			buffer = buffer[size..];

			newPacket.HeaderRef.frg = IsStreamMode
				? (byte)(packetCount - i - 1) // 6packets idx: [5, 4, 3, 2, 1, 0] 
				: (byte)0;

			sent += size;

			packetOfLastFragment = newPacket;
			snd_queue.Enqueue(newPacket);
		}

		if (source is not null)
		{
			packetOfLastFragment?.SetOnPacketFinished(source, ct);
			return KcpSendResult.Succeed(sent, source);
		}
		else
		{
			return KcpSendResult.Succeed(sent);
		}
	}

	#region 从底层传输输入数据
	// 我觉得接收时不会有ReadOnlySequence传进来的情况，毕竟不是PipeReader读取，而是DatagramSocket接收数据。实在有那就让他循环调用。
	/// <summary>
	/// 从底层传输输入数据
	/// </summary>
	/// <param name="buffer">底层传输处获得的KCP数据流</param>
	/// <returns>输入结果，包含接受的数据长度。若没有全部接受，可能需要缓存数据。</returns>
	protected KcpInputResult InputFromUnderlyingTransport(ReadOnlyMemory<byte> buffer)
	{
		ObjectDisposedException.ThrowIf(IsDisposed, nameof(KcpConnectionBase));

		if (buffer.Length < IKCP_OVERHEAD)
		{
			return new(-1);
		}


		uint prev_una = snd_una;
		var offset = 0;
		int anyAckReceived = 0;// 原flag
		uint maxack = 0;
		uint latest_ts = 0;

		while (true)
		{
			if (buffer.Length - offset < IKCP_OVERHEAD)
				break;

			KcpPacketHeader? headerSafe = KcpPacketHeaderAnyEndian.DecodeToMachineForm(buffer.Span);

			if (headerSafe is null)
			{
				return new(-1);
			}

			KcpPacketHeaderAnyEndian header = headerSafe.Value.ValueAnyEndian;
			// var dataBuffer = buffer.Slice(offset); 交给下面

			if (header.conv != conv)
				return new(-1);

			if (buffer.Length < header.len + IKCP_OVERHEAD)
				return new(-3);

			switch (header.cmd)
			{
				case KcpCommand.Ack:
				case KcpCommand.Push:
				case KcpCommand.WindowProbe:
				case KcpCommand.WindowSizeTell:
					break;
				default:
					return new(-3);
			}

			rmt_wnd = header.wnd;
			ParseUnacknowedged(header.una);
			ShrinkBuf();

			if (header.cmd == KcpCommand.Ack)
			{
				int timeDiff = TimeDiffSigned(current, header.ts);
				if (timeDiff >= 0)
					UpdateAck(timeDiff);

				ParseAck(header.sn);
				ShrinkBuf();

				if (anyAckReceived == 0)
				{
					anyAckReceived = 1;
					maxack = header.sn;
					latest_ts = header.ts;
				}
				else if (TimeDiffSigned(header.sn, maxack) > 0)
				{
					maxack = header.sn;
					latest_ts = header.ts;
				}

				// TODO ETW Logs
			}
			else if (header.cmd == KcpCommand.Push)
			{
				// TODO ETW Logs

				if (TimeDiffSigned(header.sn, rcv_nxt + rcv_wnd) < 0)
				{
					if (!isWindowFull)
					{
						acklist.Enqueue((header.sn, header.ts));

						if (TimeDiffSigned(header.sn, rcv_nxt) >= 0)
						{
							int packetLength = IKCP_OVERHEAD + (int)header.len;
							var packet = PacketBuffer.FromNetwork(buffer[..packetLength], ArrayPool<byte>.Shared);

							buffer = buffer[packetLength..];
							ParseData(packet);
						}
					}
					else
					{
						int packetTotalSize = IKCP_OVERHEAD + (int)header.len;
						buffer = buffer[packetTotalSize..];
						offset += packetTotalSize;
						continue;
					}
				}
				else
				{
					// 窗口外数据：仍发送ACK
					acklist.Enqueue((header.sn, header.ts));
				}
			}
			else if (header.cmd == KcpCommand.WindowProbe)
			{
				probe |= AskType.Tell;
				// TODO ETW Logs
			}
			else if (header.cmd == KcpCommand.WindowSizeTell)
			{
				// do nothing, log to ETW
			}
			else
			{
				return new(-3);
			}

			offset += (int)header.len + IKCP_OVERHEAD;
		}

		if (anyAckReceived != 0)
		{
			ParseFastAck(maxack, latest_ts);
		}

		if (TimeDiffSigned(snd_una, prev_una) > 0)
		{
			if (cwnd < rmt_wnd)
			{
				cwnd++;
				incr = mss;
			}
			else
			{
				if (incr < mss)
					incr = mss;

				incr += (mss * mss) / incr * (mss / 16);

				if ((cwnd + 1) * mss <= incr)
				{
					cwnd = (incr + mss - 1) / ((mss > 0) ? mss : 1);
				}
			}

			if (cwnd > rmt_wnd)
			{
				cwnd = rmt_wnd;
				incr = rmt_wnd * mss;
			}
		}

		return new(0);
	}

	/// <summary>
	/// 更新窗口满状态
	/// </summary>
	private void UpdateWindowFullState()
	{
		uint totalPackets = (uint)(rcv_queue.Count + rcv_buf.Count);
		uint effectiveWindow = Math.Min(rcv_wnd, maxRcvWindow);

		bool newState = totalPackets >= effectiveWindow;

		if (isWindowFull != newState)
		{
			isWindowFull = newState;
			Trace.WriteLine($"[FaGe.KCP] 连接 {conv} 窗口状态变化: " +
				$"{(newState ? "已满" : "有空闲")} ({totalPackets}/{effectiveWindow})");
		}
	}

	private void ParseFastAck(uint sn, uint latest_ts)
	{
		if (TimeDiffSigned(sn, snd_una) < 0 || TimeDiffSigned(sn, snd_nxt) >= 0)
		{
			return;
		}

		foreach (var packet in snd_buf)
		{
			if (TimeDiffSigned(sn, packet.HeaderRef.sn) < 0)
			{
				break;
			}
			else if (sn != packet.HeaderRef.sn)
			{
#if !IKCP_FASTACK_CONSERVE
				packet.PacketControlFields.fastack++;
#else
					if (TimeDiffSigned(ts, seg.ts) >= 0)
					{
						packet.PacketControlFields.fastack++;
					}
#endif
			}
		}
	}

	private void ParseData(PacketBuffer packet)
	{
		uint sn = packet.HeaderRef.sn;

		if (TimeDiffSigned(sn, rcv_nxt + rcv_wnd) >= 0 ||
			TimeDiffSigned(sn, rcv_nxt) < 0)
		{
			packet.Dispose();
			return;
		}

		bool isRepeat = false;
		LinkedListNode<PacketBuffer>? p;
		for (p = rcv_buf.Last; p != null; p = p.Previous)
		{
			var checkingPacket = p.Value;
			if (checkingPacket.HeaderRef.sn == packet.HeaderRef.sn)
			{
				isRepeat = true;
				break;
			}

			if (TimeDiffSigned(sn, packet.HeaderRef.sn) > 0)
			{
				break;
			}
		}

		if (!isRepeat)
		{
			// TODO ETW Log, write header

			if (p == null)
			{
				if (packet.HeaderRef.frg + 1 > rcv_wnd)
				{
					InvokeOnConnectionWasClosed();
					// 这里不要dispose，让上层处理
					throw new KcpInputException($"sn={packet.HeaderRef.sn}的分片包" +
						$"（{packet.HeaderRef.frg + 1}片），分片长度超过接收窗口，无法继续接收。", 1);
				}

				rcv_buf.AddFirst(packet);
			}
			else
			{
				rcv_buf.AddAfter(p, packet);
			}
		}

		MoveReceiveBufferToReceiveQueue();
	}

	private void MoveReceiveBufferToReceiveQueue()
	{
		while (rcv_buf.Count > 0)
		{
			var first = rcv_buf.First!;
			var firstPacket = first.Value;
			var firstSn = firstPacket.HeaderRef.sn;
			if (firstSn == rcv_nxt && rcv_queue.Count < RecvWindowSize)
			{
				rcv_buf.RemoveFirst();
				rcv_queue.Enqueue(firstPacket);

				rcv_nxt++;
			}
			else
			{
				break;
			}
		}
		pendingReceiveTaskSource?.SetResult();
	}

	private void ParseAck(uint sn)
	{
		if (TimeDiffSigned(sn, snd_una) < 0 ||
			TimeDiffSigned(sn, snd_nxt) >= 0)
		{
			return;
		}

		for (var p = snd_buf.First; p != null; p = p.Next)
		{
			var packet = p.Value;
			if (sn == packet.HeaderRef.sn)
			{
				snd_buf.Remove(p);

				// 通知SendAsync()该报文已送达，准备收回控制权
				packet.SetAsnycCompleted();
				packet.Dispose();
				break;
			}

			if (TimeDiffSigned(sn, packet.HeaderRef.sn) < 0)
			{
				break;
			}
		}
	}

	private void UpdateAck(int rtt)
	{
		if (rx_srtt == 0)
		{
			rx_srtt = (uint)rtt;
			rx_rttval = (uint)rtt / 2;
		}
		else
		{
			int delta = (int)((uint)rtt - rx_srtt);

			if (delta < 0)
			{
				delta = -delta;
			}

			rx_rttval = (3 * rx_rttval + (uint)delta) / 4;
			rx_srtt = (uint)((7 * rx_srtt + rtt) / 8);

			if (rx_srtt < 1)
			{
				rx_srtt = 1;
			}
		}

		var rto = rx_srtt + Math.Max(interval, 4 * rx_rttval);

		rx_rto = Bound(rx_minrto, rto, IKCP_RTO_MAX);
	}

	private static uint Bound(uint lower, uint middle, uint upper)
		=> Math.Min(Math.Max(lower, middle), upper);

	private void ShrinkBuf()
	{
		snd_una = snd_buf.Count > 0 ? snd_buf.First!.Value.HeaderRef.sn : snd_nxt;
	}

	protected void ParseUnacknowedged(uint una)
	{
		// p = p.Next看着吓人但是目前版本(NET10)能行
		for (var p = snd_buf.First; p != null; p = p.Next)
		{
			PacketBuffer packet = p.Value;
			if (TimeDiffSigned(una, packet.HeaderRef.sn) > 0)
			{
				snd_buf.Remove(p);

				packet.SetAsnycCompleted();
				packet.Dispose();
			}
			else
			{
				break;
			}
		}
	}
	#endregion

	#region 传递数据到上层
	public KcpApplicationPacket TryReadPacket(CancellationToken cancellationToken)
	{
		if (rcv_queue.Count == 0)
			return default; // 使source为null，确保不会调用Advance

		ReadOnlySequence<byte> result = GetFirstPacketMemory(out int fragmentCount);

		if (fragmentCount == -1)
			return default;

		ReadResult readResult = new(result, IsDisposed, cancellationToken.IsCancellationRequested);

		return new KcpApplicationPacket(readResult, this, fragmentCount);
	}

	// 我有预感，这个method会变得难以理解
	private ReadOnlySequence<byte> GetFirstPacketMemory(out int fragmentCount)
	{
		SimpleSegment? segHead = null;
		SimpleSegment? segTail = null;
		PacketBuffer packet = rcv_queue.Peek();
		Debug.Assert(packet.IsMachineEndian, "此包未经处理，直接传入了上层？");
		byte frg = packet.HeaderRef.frg;
		if (frg == 0)
		{
			fragmentCount = 0;
			return new(packet.PayloadMemory);
		}
		else
		{
			// 这就是选择Queue<T>的下场
			if (rcv_queue.Count < frg + 1)
			{
				fragmentCount = -1;
				return default;
			}
			else
			{
				// 第一分片转换为头节点
				segHead = segTail = new SimpleSegment(packet.PayloadMemory, 0);
				rcv_queue.Dequeue();

				// 转换后续分片。
				// frg 0 1 ...x   eos
				// pkt 1 2 ...x+1 eos
				// idx 0 1 ...x   eos
				for (fragmentCount = 1; fragmentCount <= frg; fragmentCount++)
				{
					packet = rcv_queue.Dequeue();
					Debug.Assert(packet.IsMachineEndian, "此包未经处理，直接传入了上层？");
					segTail = segTail.SetNext(packet.PayloadMemory);
				}

				return new ReadOnlySequence<byte>(segHead, 0, segTail, segTail.Memory.Length);
			}
		}
	}

	internal void AdvanceFragment(int packetFragmentsCount)
	{
		for (int i = 0; i <= packetFragmentsCount; i++)
		{
			if (rcv_queue.Count == 0)
			{
				Debug.Assert(rcv_queue.Count != 0, "AdvanceFragment called more than available fragments. Thread race or internal bugs?");
				var ex = new InvalidOperationException("[FaGe.Kcp] 内部错误，需要释放的分片数量超过已接收的分片数量。" + Environment.NewLine +
					$"\t可能的原因是：多线程竞争同一连接，或FaGe.Kcp内部存在bug。请查看{nameof(Exception)}.{nameof(Exception.Data)}获取更多信息");

				ex.Data["ConnectionId"] = conv;
				ex.Data["Instance"] = this;
				ex.Data["CurrentThreadId"] = Environment.CurrentManagedThreadId;

				throw ex;
			}
			var packet = rcv_queue.Dequeue();
			UpdateWindowFullState();
			packet.Dispose();
		}
	}
	#endregion

	#region 异步收发API
	/// <summary>
	/// 将指定的数据排入内部发送队列并返回表示发送结果的异步任务。
	/// 若数据能够立即完成排队并且不存在需等待的异步发送任务，则返回已完成的 <see cref="ValueTask{KcpSendResult}"/>；
	/// 否则返回一个在内部异步发送任务完成后产生最终 <see cref="KcpSendResult"/> 的 <see cref="ValueTask{KcpSendResult}"/>。
	/// </summary>
	/// <remarks>
	/// 本方法为发送操作的基础实现：它不会直接执行网络 I/O，而是将数据封装并放入发送队列（由 <see cref="QueueToSenderWithAsyncSource(ReadOnlyMemory{byte}, TaskCompletionSource?, CancellationToken)"/> 处理）。
	/// 返回的 <see cref="ValueTask{T}"/> 在到达发送缓冲区时完成。调用方应使用返回的结果判断实际发送状态。
	/// 注意：FaGe.Kcp 的大部分实例方法并发不安全，调用方应保证对同一连接的调用为非并发的。
	/// </remarks>
	/// <param name="buffer">要发送的应用层数据（只读内存）。方法会将其分片并排入发送队列，传入的数据在调用后仍由调用方管理其生命周期。</param>
	/// <param name="cancellationToken">用于取消等待发送完成的令牌。若在等待前或等待期间发生取消，将在放出控制权前抛出 <see cref="OperationCanceledException"/>，以避免潜在死锁。</param>
	/// <returns>一个 <see cref="ValueTask{KcpSendResult}"/>，其结果包含已排队的字节数以及（可选的）表示实际发送完成的内部任务信息。</returns>
	/// <exception cref="OperationCanceledException">当 <paramref name="cancellationToken"/> 在等待前或等待期间已取消时抛出。</exception>
	protected ValueTask<KcpSendResult> SendAsyncBase(
		ReadOnlyMemory<byte> buffer,
		CancellationToken cancellationToken)
	{
		TaskCompletionSource signalSource = new();
		Task signal = signalSource.Task;
		var result = QueueToSenderWithAsyncSource(buffer, signalSource, cancellationToken);

		if (signal.IsCompleted)
		{
			return ValueTask.FromResult(result);
		}
		else
		{
			static async ValueTask<KcpSendResult> SendAsyncCore(KcpSendResult result, CancellationToken cancellationToken)
			{
				// 若取消，放出控制权之前throw，避免死锁
				cancellationToken.ThrowIfCancellationRequested();

				Debug.Assert(result.asyncSendTask != null, "");
				await result.asyncSendTask;

				return result;
			}

			return SendAsyncCore(result, cancellationToken);
		}
	}

	/// <summary>
	/// Asynchronously receives the next application-level packet from the connection.
	/// </summary>
	/// <remarks>If a packet is already available, the method returns it immediately. Otherwise, the returned task
	/// completes when a new packet arrives or the operation is canceled. This method is intended for use by derived
	/// classes to implement custom receive logic.</remarks>
	/// <param name="cancellationToken">A cancellation token that can be used to cancel the receive operation.</param>
	/// <returns>A value task that represents the asynchronous receive operation. The result contains the next available application
	/// packet, or an empty packet if the connection is closed or no data is available.</returns>
	protected ValueTask<KcpApplicationPacket> ReceiveAsyncBase(CancellationToken cancellationToken)
	{
		var appPacket = TryReadPacket(cancellationToken);
		if (appPacket.IsNotEmpty)
			return ValueTask.FromResult(appPacket);

		TaskCompletionSource signalSource = new();
		Task signal = signalSource.Task;
		pendingReceiveTaskSource = signalSource;

		if (signal.IsCompleted)
		{
			pendingReceiveTaskSource = null;
			return ValueTask.FromResult(TryReadPacket(cancellationToken));
		}
		else
		{
			static async ValueTask<KcpApplicationPacket> ReceiveAsyncCore(KcpConnectionBase kcp, TaskCompletionSource currentRecvSource, CancellationToken cancellationToken)
			{
				// 若取消，放出控制权之前throw，避免死锁
				cancellationToken.ThrowIfCancellationRequested();
				cancellationToken.Register(() =>
				{
					currentRecvSource.TrySetCanceled(cancellationToken);
				});

				Debug.Assert(kcp.pendingReceiveTaskSource == currentRecvSource, "Race condition or internal bugs detected.");
				if (kcp.pendingReceiveTaskSource != currentRecvSource)
				{
					var ex = new InvalidOperationException("[FaGe.Kcp] 异步接收内部状态不一致。" + Environment.NewLine +
						$"\t可能是由竞态条件或内部bug导致的，详情见{nameof(Exception)}.{nameof(Exception.Data)}");

					ex.Data["ConnectionId"] = kcp.conv;
					ex.Data["Instance"] = kcp;
					ex.Data["CurrentThreadId"] = Environment.CurrentManagedThreadId;
					ex.Data["ExpectedReceiveSource"] = currentRecvSource;
					ex.Data["CurrentReceiveSource"] = kcp.pendingReceiveTaskSource;

					throw ex;
				}

				await currentRecvSource.Task;
				kcp.pendingReceiveTaskSource = null;
				return kcp.TryReadPacket(default); // no cancellation here
			}

			return ReceiveAsyncCore(this, signalSource, cancellationToken);
		}
	}

	#endregion

	/// <summary>
	/// 更新时钟以检测传输状态，并检测是否需要执行刷新操作，适合进阶场景。
	/// </summary>
	/// <param name="timeMillisecNow">现在的时间戳，以毫秒为单位。</param>
	/// <returns>是否需要立即调用<see cref="FlushAsync(CancellationToken)"/>执行刷新操作。</returns>
	public bool Update(uint timeMillisecNow)
	{
		ObjectDisposedException.ThrowIf(IsDisposed, nameof(KcpConnectionBase));

		current = timeMillisecNow;

		if (!updated)
		{
			updated = true;
			ts_flush = current;
		}

		int slap = TimeDiffSigned(timeMillisecNow, ts_flush);

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

			return true;
		}
		else
		{
			return false;
		}
	}

	/// <summary>
	/// 更新时钟以检测传输状态，并自动按需执行刷新操作。
	/// </summary>
	/// <param name="timeMillisecNow">现在的时间戳，以毫秒为单位。</param>
	public ValueTask UpdateAsync(uint timeMillisecNow, CancellationToken ct)
	{
		if (!Update(timeMillisecNow))
			return ValueTask.CompletedTask;
		else
			return FlushAsync(ct);
	}

	/// <summary>
	/// ikcp_check
	/// </summary>
	/// <param name="nowMillisec"></param>
	/// <returns></returns>
	public uint GetWhenShouldUpdate(uint nowMillisec)
	{
		ObjectDisposedException.ThrowIf(IsDisposed, nameof(KcpConnectionBase));

		uint ts_flushLocal = ts_flush;
		int tm_flush = int.MaxValue;
		int tm_packet = int.MaxValue;
		uint minimal = 0;

		if (updated)
		{
			return nowMillisec;
		}

		if (TimeDiffSigned(nowMillisec, ts_flushLocal) >= 10000
			|| TimeDiffSigned(nowMillisec, ts_flushLocal) < -10000)
		{
			ts_flush = current;
		}

		if (TimeDiffSigned(nowMillisec, ts_flushLocal) >= 0)
		{
			return nowMillisec;
		}

		tm_flush = TimeDiffSigned(ts_flushLocal, nowMillisec);

		foreach (var segment in snd_buf)
		{
			int diff = TimeDiffSigned(segment.PacketControlFields.resendts, nowMillisec);
			if (diff <= 0)
			{
				return nowMillisec;
			}
			if (diff < tm_packet)
			{
				tm_packet = diff;
			}
		}

		minimal = (uint)(tm_packet < tm_flush ? tm_packet : tm_flush);
		if (minimal >= interval)
		{
			minimal = interval;
		}

		return current + minimal;
	}


	/// <summary>
	/// 异步执行IO操作，通常在更新时钟时已经执行。
	/// </summary>
	public async ValueTask FlushAsync(CancellationToken ct)
	{
		ObjectDisposedException.ThrowIf(IsDisposed, nameof(KcpConnectionBase));

		// 确保窗口状态是最新的
		UpdateWindowFullState();

		// 我们很难在这里使用局部变量来创建临时缓冲区，因为源自stackalloc的内存必须在堆栈上分配，无法在异步点跨越堆栈边界。
		// 因此，我们使用一个租赁的缓冲区来存储数据，直到调用输出回调。
		// `current`, store
		uint tickNow = current;

		int change = 0;
		bool lost = false;

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

		// 发送控制包
		// 估测将使用3个包大小的buffer
		flushBuffer.EnsureCapacity(ControlPacketBufferSize);

		while (acklist.TryDequeue(out var ack))
		{
			KcpPacketHeaderAnyEndian ackHeader = genericHeader.ValueAnyEndian with { sn = ack.sn, ts = ack.ts };

			if (flushBuffer.EncodedPacketsMemory.Length + IKCP_OVERHEAD > MTU)
			{
				// 已编码+当前的ACK数量超过MTU，调用输出回调
				await InvokeOutputCallbackAsync(flushBuffer.EncodedPacketsMemory, ct);
				flushBuffer.Reset();
			}

			if (!flushBuffer.TryWriteHeaderOnlyPacket(ackHeader))
			{
				await InvokeOutputCallbackAsync(flushBuffer.EncodedPacketsMemory, ct);
				flushBuffer.Reset();
				bool result = flushBuffer.TryWriteHeaderOnlyPacket(ackHeader);
				Debug.Assert(result, "无法写入ACK包到空的flushBuffer中，内部bug或设置不当？");
				throw new ArgumentException("无法写入ACK包到空的flushBuffer中，可能是MTU,MSS设置过小。", nameof(MTU));
			}
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
			KcpPacketHeaderAnyEndian probeSendHeader = genericHeader.ValueAnyEndian with { cmd = KcpCommand.WindowProbe };

			if (flushBuffer.EncodedPacketsMemory.Length + IKCP_OVERHEAD > MTU)
			{
				// 已编码+当前的ACK数量超过MTU，调用输出回调
				await InvokeOutputCallbackAsync(flushBuffer.EncodedPacketsMemory, ct);
				flushBuffer.Reset();
			}

			if (!flushBuffer.TryWriteHeaderOnlyPacket(probeSendHeader))
			{
				await InvokeOutputCallbackAsync(flushBuffer.EncodedPacketsMemory, ct);
				flushBuffer.Reset();
				bool result = flushBuffer.TryWriteHeaderOnlyPacket(probeSendHeader);
				Debug.Assert(result, "无法写入PROBE包到空的flushBuffer中，内部bug或设置不当？");
				throw new ArgumentException("无法写入PROBE包到空的flushBuffer中，可能是MTU,MSS设置过小。", nameof(MTU));
			}
		}

		// 发送探测包（通知远端我们的窗口大小）
		if ((probe & AskType.Tell) == AskType.Tell)
		{
			KcpPacketHeaderAnyEndian probeTellHeader = genericHeader.ValueAnyEndian with { cmd = KcpCommand.WindowSizeTell };

			if (flushBuffer.EncodedPacketsMemory.Length + IKCP_OVERHEAD > MTU)
			{
				// 已编码+当前的ACK数量超过MTU，调用输出回调
				await InvokeOutputCallbackAsync(flushBuffer.EncodedPacketsMemory, ct);
				flushBuffer.Reset();
			}

			if (!flushBuffer.TryWriteHeaderOnlyPacket(probeTellHeader))
			{
				await InvokeOutputCallbackAsync(flushBuffer.EncodedPacketsMemory, ct);
				flushBuffer.Reset();
				bool result = flushBuffer.TryWriteHeaderOnlyPacket(probeTellHeader);
				Debug.Assert(result, "无法写入PROBE包到空的flushBuffer中，内部bug或设置不当？");
				throw new ArgumentException("无法写入PROBE包到空的flushBuffer中，可能是MTU,MSS设置过小。", nameof(MTU));
			}

		}

		probe = AskType.None;

		uint cwndLocal = Math.Min(snd_wnd, rmt_wnd);
		if (NoCwnd)
		{
			cwndLocal = Math.Min(cwnd, cwndLocal);
		}

		await InvokeOutputCallbackAsync(flushBuffer.EncodedPacketsMemory, ct);
		flushBuffer.Reset();

		// 将数据包从发送队列移动到发送缓冲区
		// 这里没有涉及IO操作，因此可以同步执行
		while (TimeDiffSigned(snd_nxt, snd_una + cwndLocal) < 0)
		{
			if (snd_queue.TryDequeue(out var packet))
			{
				var pushHeader = genericHeader.ValueAnyEndian with
				{
					cmd = KcpCommand.Push,
					// wnd 已经在genericHeader中设置
					ts = tickNow,
					sn = snd_nxt,
					una = rcv_nxt,

				};

				packet.HeaderRef = pushHeader;

				packet.PacketControlFields.resendts = tickNow;
				packet.PacketControlFields.rto = rx_rto;
				packet.PacketControlFields.fastack = 0;
				packet.PacketControlFields.xmit = 0;

				snd_buf.AddLast(packet);
				snd_nxt++;

			}
			else
			{
				break;
			}
		}

		var resent = fastresend > 0 ? (uint)fastresend : 0xffffffff;
		var rtomin = IsNoDelayMode ? (rx_rto >> 3) : 0;

		// flush data segments
		for (var node = snd_buf.First; node != null; node = node.Next)
		{
			var packet = node.Value;
			var needsend = false;

			if (packet.PacketControlFields.xmit == 0)
			{
				//新加入 snd_buf 中, 从未发送过的报文直接发送出去;
				needsend = true;
				packet.PacketControlFields.xmit++;
				packet.PacketControlFields.rto = rx_rto;
				packet.PacketControlFields.resendts = tickNow + rx_rto + rtomin;
			}
			else if (TimeDiffSigned(tickNow, packet.PacketControlFields.resendts) >= 0)
			{
				//发送过的, 但是在 RTO 内未收到 ACK 的报文, 需要重传;
				needsend = true;
				packet.PacketControlFields.xmit++;
				xmit++;
				if (IsNoDelayMode)
				{
					packet.PacketControlFields.rto += Math.Max(packet.PacketControlFields.rto, rx_rto);
				}
				else
				{
					var step = nodelay < 2 ? packet.PacketControlFields.rto : rx_rto;
					packet.PacketControlFields.rto += step / 2;
				}

				packet.PacketControlFields.resendts = tickNow + packet.PacketControlFields.rto;
				lost = true;
			}
			else if (packet.PacketControlFields.fastack >= resent)
			{
				//发送过的, 但是 ACK 失序若干次的报文, 需要执行快速重传.
				if (packet.PacketControlFields.xmit <= fastlimit
					|| fastlimit <= 0)
				{
					needsend = true;
					packet.PacketControlFields.xmit++;
					packet.PacketControlFields.fastack = 0;
					packet.PacketControlFields.resendts = tickNow + packet.PacketControlFields.rto;
					change++;
				}
			}

			if (needsend)
			{
				// 通知上层该报文将被发送（或重传）
				if (ct.IsCancellationRequested)
				{
					packet.SetAsnycCanceled(ct);
					throw new OperationCanceledException("FlushAsync was canceled.", ct);
				}
				packet.SetAsnycCompleted();
				packet.MarkPacketCompleted();

				packet.HeaderRef = packet.HeaderRef with
				{
					ts = tickNow,
					wnd = genericHeader.ValueAnyEndian.wnd, // 特殊方式获取方法开始时的wnd值
					una = rcv_nxt,
				};

				var need = IKCP_OVERHEAD + packet.Length;
				// using RentBuffer buffer = new((int)MTU + IKCP_OVERHEAD, ArrayPool<byte>.Shared);
				int size = flushBuffer.EncodedPacketsMemory.Length;
				if (size + need > MTU)
				{
					await InvokeOutputCallbackAsync(flushBuffer.EncodedPacketsMemory, ct);
					flushBuffer.Reset();
				}

				// offset += segment.Encode(buffer.Memory.Span.Slice(offset));
				
				if (!flushBuffer.TryWritePacket(packet))
				{
					// ETW LOG
				}

				// TODO 用EventSource改写
				//if (CanLog(KcpLogMask.IKCP_LOG_NEED_SEND))
				//{
				//	LogWriteLine($"{segment.ToLogString(true)}", KcpLogMask.IKCP_LOG_NEED_SEND.ToString());
				//}

				if (packet.PacketControlFields.xmit >= dead_link)
				{
					Dispose();
					// TODO 用EventSource改写
					//if (CanLog(KcpLogMask.IKCP_LOG_DEAD_LINK))
					//{
					//	LogWriteLine($"state = -1; xmit:{segment.xmit} >= dead_link:{dead_link}", KcpLogMask.IKCP_LOG_DEAD_LINK.ToString());
					//}
				}
			}
		}

		// flash remain segments
		await InvokeOutputCallbackAsync(flushBuffer.EncodedPacketsMemory, ct);
		flushBuffer.Reset();

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

		if (lost)
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

		if (IsDisposed)
		{
			InvokeOnConnectionWasClosed();
		}
	}

	private void CancelAllPendingAsyncOperations()
	{
		foreach (var packet in snd_buf)
		{
			packet.DisposeCts?.Cancel();
			packet.MarkPacketCompleted();
		}
		snd_buf.Clear();
		foreach (var packet in snd_queue)
		{
			packet.DisposeCts?.Cancel();
			packet.MarkPacketCompleted();
		}
		snd_queue.Clear();
		foreach (var packet in rcv_buf)
		{
			packet.DisposeCts?.Cancel();
			packet.MarkPacketCompleted();
		}
		rcv_buf.Clear();
		foreach (var packet in rcv_queue)
		{
			packet.DisposeCts?.Cancel();
			packet.MarkPacketCompleted();
		}
		rcv_queue.Clear();
	}

	/// <summary>
	/// 获取将要接收的下一个消息的总长度（包括分包拼接后的长度）。如果没有完整的消息可供接收，则返回-1。
	/// </summary>
	/// <returns>下一个待接收消息的总长度，或-1表示没有完整消息可供接收。</returns>
	// ikcp_peeksize
	public int GetNextReceivedMessageSize()
	{
		ObjectDisposedException.ThrowIf(IsDisposed, nameof(KcpConnectionBase));

		if (rcv_queue.Count == 0)
			return -1;

		PacketBuffer packet = rcv_queue.Peek();

		KcpPacketHeaderAnyEndian header = packet.HeaderRef;
		if (header.frg == 0)
			return (int)header.len;

		// 这段数据过长，需要拼接多个分包以计算总长度
		// 首先检查分包是否全部到达
		if (rcv_queue.Count < header.frg + 1)
			return -1;

		int length = (int)header.len;
		foreach (var packetAfterFirst in rcv_queue.Skip(1))
		{
			KcpPacketHeaderAnyEndian headerAfterFirst = packetAfterFirst.Header.MachineForm.ValueAnyEndian;
			length += (int)headerAfterFirst.len;

			if (headerAfterFirst.frg == 0)
				break;
		}

		return length;
	}

	/// <summary>
	/// 
	/// </summary>
	/// <param name="msecLater"></param>
	/// <param name="msecEarlier"></param>
	/// <returns></returns>
	protected static int TimeDiffSigned(uint msecLater, uint msecEarlier)
	{
		return (int)(msecLater - msecEarlier);
	}

	/// <summary>
	/// Copilot generated summary:<para/>
	/// Configures advanced transmission parameters for the protocol, including no-delay mode, update interval, fast resend
	/// behavior, and congestion control settings.
	/// </summary>
	/// <remarks>Use this method to fine-tune protocol behavior for specific network conditions or application
	/// requirements. Adjusting these parameters can affect latency, throughput, and reliability. Disabling congestion
	/// control may improve performance in controlled environments but can lead to increased packet loss on congested
	/// networks.<para/>
	/// According to the comments of origingal C implementation,
	/// the recommended fastest settings for low latency are:(true, 20, 2 true).
	/// </remarks>
	/// <param name="nodelay">Specifies whether to enable no-delay mode. If <see langword="true"/>, the protocol operates with reduced minimum
	/// retransmission timeout for lower latency.</param>
	/// <param name="interval">此参数控制刷新（flush）间隔，以毫秒为单位，用于内部协议处理的周期性刷新操作。注意，刷新操作实际由用户执行。</param>
	/// <param name="resend">Specifies max amount of fast resendable packet.</param>
	/// <param name="nc">Specifies whether to disable congestion control. If <see langword="true"/>, congestion control is turned off.</param>
	public void ConfigureNoDelay(bool nodelay, int interval, int resend, bool nc)
	{
		ObjectDisposedException.ThrowIf(IsDisposed, nameof(KcpConnectionBase));

		if (nodelay)
		{
			IsNoDelayMode = nodelay;
			if (nodelay)
			{
				rx_minrto = IKCP_RTO_NDL;
			}
			else
			{
				rx_minrto = IKCP_RTO_MIN;
			}
		}

		if (interval >= 0)
		{
			if (interval > 5000) interval = 5000;
			else if (interval < 10) interval = 10;
			this.interval = (uint)interval;
		}

		if (resend >= 0)
		{
			// fastresend可能用来保存触发快速重传所需的重复ack数量
			// 所以类型不为bool
			fastresend = resend;
		}

		NoCwnd = nc;
	}

	/// <summary>
	/// 
	/// </summary>
	/// <param name="disposing"></param>
	protected virtual void Dispose(bool disposing)
	{
		if (!disposedValue)
		{
			if (disposing)
			{
				CancelAllPendingAsyncOperations();
				DisposeBuffers();
			}

			disposedValue = true;
		}
	}

	private void DisposeBuffers()
	{
		flushBuffer.Dispose();
		foreach (var packet in snd_buf)
			packet.Dispose();
		foreach (var packet in snd_queue)
			packet.Dispose();
		foreach (var packet in rcv_buf)
			packet.Dispose();
		foreach (var packet in rcv_queue)
			packet.Dispose();
		foreach (var packet in recycledBuffers)
			packet.Dispose();

		snd_buf.Clear();
		snd_queue.Clear();
		rcv_buf.Clear();
		rcv_queue.Clear();
		recycledBuffers.Clear();
	}

	/// <summary>
	/// 
	/// </summary>
	public void Dispose()
	{
		// 不要更改此代码。请将清理代码放入“Dispose(bool disposing)”方法中
		Dispose(disposing: true);
		GC.SuppressFinalize(this);
	}
}