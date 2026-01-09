using DanmakuR.Protocol.Buffer;
using FaGe.Kcp.Utility;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using static FaGe.Kcp.KcpConst;

namespace FaGe.Kcp.Connections;

public abstract class KcpConnectionBase : IDisposable, IAsyncDisposable
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

	private RentBuffer ackFlushBuffer;

	private bool disposedValue;

	[Obsolete]
	private int stream => IsStreamMode ? 1 : 0;

	private int ThreeMtuPacketBufferSize => (int)(3 * (MTU + IKCP_OVERHEAD));

#pragma warning restore

	// Pipe传输模式开的洞
	//private readonly Pipe sendPipe = new();
	//private readonly Pipe receivePipe = new();

	//protected PipeReader ReceiveReader => receivePipe.Reader;
	//protected PipeWriter SendWriter => sendPipe.Writer;

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

		snd_buf = new();
		rcv_queue = new();

		ackFlushBuffer = new(ThreeMtuPacketBufferSize, AckOutputTemporaryBufferPool);
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
			ackFlushBuffer = new(ThreeMtuPacketBufferSize, AckOutputTemporaryBufferPool);
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
				packetOfLastFragment?.SetPacketFinished(source, ct);
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
					packetOfLastFragment?.SetPacketFinished(source, ct);
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

			newPacket.HeaderAnyEndian.frg = IsStreamMode
				? (byte)(packetCount - i - 1) // 6packets idx: [5, 4, 3, 2, 1, 0] 
				: (byte)0;

			sent += size;

			packetOfLastFragment = newPacket;
			snd_queue.Enqueue(newPacket);
		}

		if (source is not null)
		{
			packetOfLastFragment?.SetPacketFinished(source, ct);
			return KcpSendResult.Succeed(sent, source);
		}
		else
		{
			return KcpSendResult.Succeed(sent);
		}
	}

	#region 从底层传输输入数据
	// 我觉得接收时不会有ReadOnlySequence传进来的情况，毕竟不是PipeReader读取，而是DatagramSocket接收数据。实在有那就让他循环调用。
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

			if (KcpPacketHeader.TryRead(buffer.Span, out var headerDecoded))
			{
				return new(-2);
			}

			KcpPacketHeaderAnyEndian header = headerDecoded.ValueAnyEndian;
			// var dataBuffer = buffer.Slice(offset); 交给下面

			if (header.conv != conv)
				return new(-1);

			if (buffer.Length < header.len)
				return new(-2);

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
					acklist.Enqueue((header.sn, header.ts));

					if (TimeDiffSigned(header.sn, rcv_nxt) >= 0)
					{
						int packetLength = IKCP_OVERHEAD + (int)header.len;
						var packet = PacketBuffer.FromNetwork(buffer[..packetLength], ArrayPool<byte>.Shared);

						buffer = buffer[packetLength..];
						ParseData(packet);
					}
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

			offset += (int)header.len;
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

	private void ParseFastAck(uint sn, uint latest_ts)
	{
		if (TimeDiffSigned(sn, snd_una) < 0 || TimeDiffSigned(sn, snd_nxt) >= 0)
		{
			return;
		}

		foreach (var packet in snd_buf)
		{
			if (TimeDiffSigned(sn, packet.HeaderAnyEndian.sn) < 0)
			{
				break;
			}
			else if (sn != packet.HeaderAnyEndian.sn)
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
		uint sn = packet.HeaderAnyEndian.sn;

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
			if (checkingPacket.HeaderAnyEndian.sn == packet.HeaderAnyEndian.sn)
			{
				isRepeat = true;
				break;
			}

			if (TimeDiffSigned(sn, packet.HeaderAnyEndian.sn) > 0)
			{
				break;
			}
		}

		if (!isRepeat)
		{
			// TODO ETW Log, write header

			if (p == null)
			{
				if (packet.HeaderAnyEndian.frg + 1 > rcv_wnd)
				{
					InvokeOnConnectionWasClosed();
					// 这里不要dispose，让上层处理
					throw new KcpInputException($"sn={packet.HeaderAnyEndian.sn}的分片包" +
						$"（{packet.HeaderAnyEndian.frg + 1}片），分片长度超过接收窗口，无法继续接收。", 1);
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
			var firstSn = firstPacket.HeaderAnyEndian.sn;
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
			if (sn == packet.HeaderAnyEndian.sn)
			{
				snd_buf.Remove(p);
				packet.Dispose();
				break;
			}

			if (TimeDiffSigned(sn, packet.HeaderAnyEndian.sn) < 0)
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
		snd_una = snd_buf.Count > 0 ? snd_buf.First!.Value.HeaderAnyEndian.sn : snd_nxt;
	}

	protected void ParseUnacknowedged(uint una)
	{
		// p = p.Next看着吓人但是目前版本(NET10)能行
		for (var p = snd_buf.First; p != null; p = p.Next)
		{
			PacketBuffer packet = p.Value;
			if (TimeDiffSigned(una, packet.HeaderAnyEndian.sn) > 0)
			{
				snd_buf.Remove(p);

				if (packet.PacketFinished is not null)
				{
					// 通知SendAsync()该报文已送达，准备收回控制权
					packet.PacketFinished.TrySetResult();
				}
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
		byte frg = packet.HeaderAnyEndian.frg;
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
		for (int i = 0; i < packetFragmentsCount; i++)
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
			packet.Dispose();
		}
	}
	#endregion

	#region 异步收发API

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

	protected ValueTask<KcpApplicationPacket> ReceiveAsyncBase(CancellationToken cancellationToken)
	{
		var appPacket = TryReadPacket(cancellationToken);
		if (!appPacket.IsNotEmpty)
			return ValueTask.FromResult(appPacket);

		TaskCompletionSource signalSource = new();
		Task signal = signalSource.Task;
		pendingReceiveTaskSource = signalSource;

		if (signal.IsCompleted)
		{
			return ValueTask.FromResult(TryReadPacket(cancellationToken));
		}
		else
		{
			static async ValueTask<KcpApplicationPacket> ReceiveAsyncCore(KcpApplicationPacket result, KcpConnectionBase kcp, TaskCompletionSource currentRecvSource, CancellationToken cancellationToken)
			{
				// 若取消，放出控制权之前throw，避免死锁
				cancellationToken.ThrowIfCancellationRequested();

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

				return result;
			}

			return ReceiveAsyncCore(appPacket, this, signalSource, cancellationToken);
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

	// ikcp_check
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
		ackFlushBuffer.EnsureCapacity(ThreeMtuPacketBufferSize);

		Memory<byte> genericEncodingBuffer = ackFlushBuffer.Memory;
		Memory<byte> currentEncodingBuffer = genericEncodingBuffer;

		while (acklist.TryDequeue(out var ack))
		{
			// 检查是否有足够空间写入ACK包
			int size = GetEncodedBufferLength(genericEncodingBuffer, currentEncodingBuffer);
			if (size + IKCP_OVERHEAD > MTU)
			{
				// 已编码的ACK数量超过临时buffer容量，调用输出回调
				ReadOnlyMemory<byte> encodedBuffer = currentEncodingBuffer[..size];
				await InvokeOutputCallbackAsync(encodedBuffer, ct);
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
				await InvokeOutputCallbackAsync(encodedBuffer, ct);
				currentEncodingBuffer = genericEncodingBuffer;
			}
			var probeHeader = KcpPacketHeader.FromMachine(genericHeader.ValueAnyEndian with { cmd = KcpCommand.WindowProbe });
			var encodeSpan = currentEncodingBuffer.Span;
			probeHeader.Write(ref encodeSpan);
			currentEncodingBuffer = currentEncodingBuffer[..(currentEncodingBuffer.Length - encodeSpan.Length)];
		}

		// 发送探测包（通知远端我们的窗口大小）
		if ((probe & AskType.Tell) == AskType.Tell)
		{
			int size = GetEncodedBufferLength(genericEncodingBuffer, currentEncodingBuffer);
			if (size + IKCP_OVERHEAD > MTU)
			{
				// 已编码的指令数量超过临时buffer容量，调用输出回调
				ReadOnlyMemory<byte> encodedBuffer = currentEncodingBuffer[..size];
				await InvokeOutputCallbackAsync(encodedBuffer, ct);
				currentEncodingBuffer = genericEncodingBuffer;
			}
			var probeHeader = KcpPacketHeader.FromMachine(genericHeader.ValueAnyEndian with { cmd = KcpCommand.WindowSizeTell });
			var encodeSpan = currentEncodingBuffer.Span;
			probeHeader.Write(ref encodeSpan);
			currentEncodingBuffer = currentEncodingBuffer[..(currentEncodingBuffer.Length - encodeSpan.Length)];
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
				var pushHeader = genericHeader.ValueAnyEndian with
				{
					cmd = KcpCommand.Push,
					// wnd 已经在genericHeader中设置
					ts = tickNow,
					sn = snd_nxt,
					una = rcv_nxt,

				};

				packet.HeaderAnyEndian = pushHeader;

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
		foreach (var item in snd_buf)
		{
			var segment = item;
			var needsend = false;

			if (segment.PacketControlFields.xmit == 0)
			{
				//新加入 snd_buf 中, 从未发送过的报文直接发送出去;
				needsend = true;
				segment.PacketControlFields.xmit++;
				segment.PacketControlFields.rto = rx_rto;
				segment.PacketControlFields.resendts = tickNow + rx_rto + rtomin;
			}
			else if (TimeDiffSigned(tickNow, segment.PacketControlFields.resendts) >= 0)
			{
				//发送过的, 但是在 RTO 内未收到 ACK 的报文, 需要重传;
				needsend = true;
				segment.PacketControlFields.xmit++;
				xmit++;
				if (IsNoDelayMode)
				{
					segment.PacketControlFields.rto += Math.Max(segment.PacketControlFields.rto, rx_rto);
				}
				else
				{
					var step = nodelay < 2 ? segment.PacketControlFields.rto : rx_rto;
					segment.PacketControlFields.rto += step / 2;
				}

				segment.PacketControlFields.resendts = tickNow + segment.PacketControlFields.rto;
				lost = true;
			}
			else if (segment.PacketControlFields.fastack >= resent)
			{
				//发送过的, 但是 ACK 失序若干次的报文, 需要执行快速重传.
				if (segment.PacketControlFields.xmit <= fastlimit
					|| fastlimit <= 0)
				{
					needsend = true;
					segment.PacketControlFields.xmit++;
					segment.PacketControlFields.fastack = 0;
					segment.PacketControlFields.resendts = tickNow + segment.PacketControlFields.rto;
					change++;
				}
			}

			if (needsend)
			{
				// 这里是重传，之前已经通知过上层了，不需要再通知
				segment.HeaderAnyEndian = segment.HeaderAnyEndian with
				{
					ts = tickNow,
					wnd = genericHeader.ValueAnyEndian.wnd, // 特殊方式获取方法开始时的wnd值
					una = rcv_nxt,
				};

				var need = IKCP_OVERHEAD + segment.Length;
				// using RentBuffer buffer = new((int)MTU + IKCP_OVERHEAD, ArrayPool<byte>.Shared);
				int size = GetEncodedBufferLength(genericEncodingBuffer, currentEncodingBuffer);
				if (need > MTU)
				{
					await InvokeOutputCallbackAsync(currentEncodingBuffer, ct);
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

				if (segment.PacketControlFields.xmit >= dead_link)
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
				// 已编码的包数量超过临时buffer容量，调用输出回调
				ReadOnlyMemory<byte> encodedBuffer = genericEncodingBuffer[..encodedBufferLen];
				await InvokeOutputCallbackAsync(encodedBuffer, ct);
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
				InvokeOnConnectionWasClosed();
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
		ObjectDisposedException.ThrowIf(IsDisposed, nameof(KcpConnectionBase));

		if (rcv_queue.Count == 0)
			return -1;

		PacketBuffer packet = rcv_queue.Peek();

		KcpPacketHeaderAnyEndian header = packet.HeaderAnyEndian;
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

	protected virtual void Dispose(bool disposing)
	{
		if (!disposedValue)
		{
			if (disposing)
			{
				DisposeBuffers();

			}

			disposedValue = true;
		}
	}

	public async ValueTask DisposeAsync()
	{
		disposedValue = true;
		foreach (var packet in snd_buf)
			packet.DisposeCts?.Cancel();
		foreach (var packet in snd_queue)
			packet.DisposeCts?.Cancel();
		foreach (var packet in rcv_buf)
			packet.DisposeCts?.Cancel();
		foreach (var packet in rcv_queue)
			packet.DisposeCts?.Cancel();

		DisposeBuffers();
	}

	private void DisposeBuffers()
	{
		ackFlushBuffer.Dispose();
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

	public void Dispose()
	{
		// 不要更改此代码。请将清理代码放入“Dispose(bool disposing)”方法中
		Dispose(disposing: true);
		GC.SuppressFinalize(this);
	}
}