using FaGe.Kcp.Utility;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
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
	protected bool NoCwnd { get => nocwnd != 0; set => nocwnd = value ? 1 : 0; }
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

	private RentBuffer flushBuffer;

	private bool disposedValue;

	[Obsolete]
	private int stream => IsStreamMode ? 1 : 0;

	private int ThreeAckPacketBufferSize => (int)(3 * (MTU + IKCP_OVERHEAD));

#pragma warning restore

	private readonly Pipe sendPipe;
	private readonly Pipe recvPipe;

	public PipeReader ReceiveReader => recvPipe.Reader;
	public PipeWriter SendWriter => sendPipe.Writer;

	protected KcpConnectionBase(
		uint conversationId,
		KcpConnectionOptionsBase? options = null)
	{
		conv = conversationId;
		snd_wnd = IKCP_WND_SND;
		rcv_wnd = IKCP_WND_RCV;
		rmt_wnd = IKCP_WND_RCV;
		MTU = IKCP_MTU_DEF;

		// snd_queue = Channel.CreateUnbounded<PacketBuffer>(sendQueueOptions);
		// rcv_queue = Channel.CreateUnbounded<PacketBuffer>(receiveQueueOptions);
		snd_buf = new();
		rcv_queue = new();

		flushBuffer = new(ThreeAckPacketBufferSize, OutputTemporaryBufferPool);
	}

	public event Action<KcpConnectionBase>? OnDeadConnection = null;

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
			flushBuffer = new(ThreeAckPacketBufferSize, OutputTemporaryBufferPool);
		}
	}

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
		get => (int)snd_wnd;
		set
		{
			ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, nameof(RecvWindowSize));
			snd_wnd = (uint)value;
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
			int waitCount = rcv_queue.Count;

			if (waitCount < rcv_wnd)
			{
				var count = rcv_wnd - waitCount;
				return (ushort)Math.Min(count, ushort.MaxValue);
			}

			return 0;
		}
	}

	public KcpConnectionState State { get; private set; }

	/// <summary>
	/// 内部API，调用输出回调以发送数据。
	/// </summary>
	/// <remarks>
	/// 重写的实现可以固定使用特定的传输机制（如UDP套接字）输出数据，而不是由外部提供输出方案。
	/// </remarks>
	/// <param name="buffer"></param>
	/// <param name="cancellationToken"></param>
	/// <returns></returns>
	private protected abstract ValueTask InvokeOutputCallbackAsync(ReadOnlySequence<byte> buffer, CancellationToken cancellationToken);

	public ValueTask<KcpSendResult> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
	{
		ObjectDisposedException.ThrowIf(IsDisposed, nameof(KcpConnectionBase));

		int sent = 0;
		Debug.Assert(mss > 0);
		if (buffer.Length < 0)
			return ValueTask.FromResult(KcpSendResult.Fail(KcpSendStatus.EmptyBuffer));

		// append to previous segment in streaming mode (if possible)
		if (IsStreamMode)
		{
			if (snd_queue.Count >= 1)
			{
				PacketAndBuffer lastPacket = snd_queue.Last();
				if (lastPacket.Length < mss && lastPacket.Capacity >= mss)
				{
					if (!buffer.IsEmpty)
					{
						int remainingCapacity = lastPacket.Capacity - lastPacket.Length;
						if (remainingCapacity < mss)
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
			return ValueTask.FromResult(KcpSendResult.Succeed(sent));

		int packetCount;

		if (buffer.Length <= mss)
			packetCount = 1;
		else
			packetCount = (int)(buffer.Length + mss - 1) / (int)mss;

		if (packetCount >= IKCP_WND_RCV)
		{
			if (IsStreamMode && sent > 0)
			{
				return ValueTask.FromResult(KcpSendResult.Succeed(sent));
			}
			else
			{
				new ConvertValueToValueTaskSource()
				{
					
				}
			}
		}

		// 标准化packetCount，防止为0
		packetCount = packetCount == 0 ? 1 : packetCount;

		for (int i = 0; i < packetCount; i++)
		{
			int size = buffer.Length > (int)mss ? (int)mss : buffer.Length;
			PacketAndBuffer newPacket = new(default, ArrayPool<byte>.Shared);
			newPacket.RentBufferFromPool(size);

			buffer.Span.Slice(0, size).CopyTo(newPacket.RemainingMemory.Span);
			buffer = buffer[size..];

			newPacket.Header.ValueAnyEndian.frg = IsStreamMode
				? (byte)(packetCount - i - 1)
				: (byte)0;

			newPacket.Advance(size);
			snd_queue.Enqueue(newPacket);
		}

		return KcpSendResult.Succeed(sent);
	}

	public ValueTask<KcpSequenceSendResult> SendAsync(ReadOnlySequence<byte> buffer)
	{
		if (buffer.IsSingleSegment)
		{
			return Send(buffer.First);
		}
		else
		{
			long sent = 0;
			foreach (var segment in buffer)
			{
				KcpSendResult result = Send(segment);
				if (!result.IsSucceed)
				{
					return new(sent, result.FailReasoon);
				}
				sent += result.SentCount.Value;
			}

			return KcpSequenceSendResult.Succeed(sent);
		}
	}

	protected (int? BytesProceed, bool IsFailed) InputFromUnderlyingTransport(ReadOnlyMemory<byte> buffer)
	{

	}


	/// <summary>
	/// 异步更新时钟，并根据需要执行刷新操作。
	/// </summary>
	/// <param name="timeMillisecNow">现在的时间戳</param>
	/// <param name="cancellationToken">用于取消更新操作的<see cref="CancellationToken"/></param>
	/// <returns></returns>
	public ValueTask UpdateAsync(uint timeMillisecNow, CancellationToken cancellationToken)
	{
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
			// 魔改部分：调用异步刷新
			return FlushAsync(cancellationToken);
		}
		else
		{
			return ValueTask.CompletedTask;
		}
	}

	// ikcp_check
	public uint GetWhenShouldUpdate(uint nowMillisec)
	{
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
			int diff = TimeDiffSigned(segment.resendts, nowMillisec);
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
	/// 异步执行IO操作，通常在更新时钟之后执行。
	/// </summary>
	public async ValueTask FlushAsync(CancellationToken ct)
	{
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

		// 发送ACK包
		// 估测将使用3个包大小的buffer
		flushBuffer.EnsureCapacity(ThreeAckPacketBufferSize);

		Memory<byte> genericEncodingBuffer = flushBuffer.Memory;
		Memory<byte> currentEncodingBuffer = genericEncodingBuffer;

		while (acklist.TryDequeue(out var ack))
		{
			// 检查是否有足够空间写入ACK包
			int size = GetEncodedBufferLength(genericEncodingBuffer, currentEncodingBuffer);
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
			int size = GetEncodedBufferLength(genericEncodingBuffer, currentEncodingBuffer);
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
		var rtomin = IsNoDelayMode ? (rx_rto >> 3) : 0;

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
				if (IsNoDelayMode)
				{
					segment.rto += Math.Max(segment.rto, rx_rto);
				}
				else
				{
					var step = nodelay < 2 ? segment.rto : rx_rto;
					segment.rto += step / 2;
				}

				segment.resendts = tickNow + segment.rto;
				lost = true;
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
				int size = GetEncodedBufferLength(genericEncodingBuffer, currentEncodingBuffer);
				if (+need > MTU)
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
					// TODO Log
				}

				// TODO 用EventSource改写
				//if (CanLog(KcpLogMask.IKCP_LOG_NEED_SEND))
				//{
				//	LogWriteLine($"{segment.ToLogString(true)}", KcpLogMask.IKCP_LOG_NEED_SEND.ToString());
				//}

				if (segment.xmit >= dead_link)
				{
					state = -1;

					// TODO 用EventSource改写
					//if (CanLog(KcpLogMask.IKCP_LOG_DEAD_LINK))
					//{
					//	LogWriteLine($"state = -1; xmit:{segment.xmit} >= dead_link:{dead_link}", KcpLogMask.IKCP_LOG_DEAD_LINK.ToString());
					//}
				}
			}

			// flash remain segments
			int encodedBufferLen = GetEncodedBufferLength(genericEncodingBuffer, currentEncodingBuffer);
			if (encodedBufferLen > 0)
			{
				// 已编码的指令数量超过临时buffer容量，调用输出回调
				ReadOnlyMemory<byte> encodedBuffer = currentEncodingBuffer[..encodedBufferLen];
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

			if (state == -1)
			{
				OnDeadConnection?.Invoke(this);
			}
		}

		static int GetEncodedBufferLength(ReadOnlyMemory<byte> all, ReadOnlyMemory<byte> remaining)
		{
			return all.Length - remaining.Length;
		}
	}

	/// <summary>
	/// 获取将要接收的下一个消息的总长度（包括分包拼接后的长度）。如果没有完整的消息可供接收，则返回-1。
	/// </summary>
	/// <returns>下一个待接收消息的总长度，或-1表示没有完整消息可供接收。</returns>
	// ikcp_peeksize
	public int GetNextReceivedMessageSize()
	{
		if (rcv_queue.Count == 0)
			return -1;

		PacketAndBuffer packet = rcv_queue[0];
		KcpPacketHeaderAnyEndian header = packet.Header.MachineForm.ValueAnyEndian;
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
	/// <param name="interval">The update interval, in milliseconds, for internal protocol processing. Default value is 100ms.</param>
	/// <param name="resend">Specifies whether to enable fast resend.</param>
	/// <param name="nc">Specifies whether to disable congestion control. If <see langword="true"/>, congestion control is turned off.</param>
	public void ConfigureNoDelay(bool nodelay, int interval, int resend, bool nc)
	{
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



	protected virtual void Dispose(bool disposing)
	{
		if (!disposedValue)
		{
			if (disposing)
			{
				// TODO: 释放托管状态(托管对象)
				flushBuffer.Dispose();
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
