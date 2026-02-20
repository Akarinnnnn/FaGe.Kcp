using System.Diagnostics.Tracing;

namespace FaGe.Kcp.Tracing;

[EventSource(Name = "FaGe.Kcp", Guid = "762347F4-61D5-4F4E-AA18-C3CA84F725B6")]
internal class KcpTraceEventSource : EventSource
{
	internal static readonly KcpTraceEventSource Log = new KcpTraceEventSource();

	[Flags]
	internal enum KcpEventKeywords : long
	{
		None = 0x0,
		Internal = 0x1,
		Api = 0x2,
		NeedSend = 0x4,
		DeadLink = 0x8,
		Buffer = 0x10,
	}

	[Event(1, Level = EventLevel.Informational, Keywords = (EventKeywords)KcpEventKeywords.Api)]
	public void KcpInputResult(int rawResult, uint connectionId)
	{
		WriteEvent(1, rawResult, connectionId);
	}

	[Event(2, Level = EventLevel.Informational, Keywords = (EventKeywords)KcpEventKeywords.Api)]
	public void KcpReceiveFailed(int rawResult, uint connectionId)
	{
		WriteEvent(2, rawResult, connectionId);
	}

	[Event(3, Level = EventLevel.Informational, Keywords = (EventKeywords)KcpEventKeywords.Api)]
	public void KcpUpdateBegin(uint connectionId, uint timeticks)
	{
		WriteEvent(3, connectionId, timeticks);
	}

	[Event(4, Level = EventLevel.Informational, Keywords = (EventKeywords)KcpEventKeywords.Api)]
	public void KcpFlush(uint connectionId)
	{
		WriteEvent(4, connectionId);
	}

	[Event(5, Level = EventLevel.Informational, Keywords = (EventKeywords)KcpEventKeywords.Api)]
	public void KcpSendEnd(uint connectionId, int length, int rawErrorCode)
	{
		WriteEvent(5, connectionId, length, rawErrorCode);
	}

	[Event(6, Level = EventLevel.Informational, Keywords = (EventKeywords)KcpEventKeywords.Api, Message = "连接 {0} 尝试获取数据包，长度：{1}")]
	public void KcpReceived(uint connectionId, int length)
	{
		WriteEvent(6, connectionId, length);
	}

	[Event(7, Level = EventLevel.Informational, Keywords = (EventKeywords)KcpEventKeywords.Api, Message = "连接 {0} 调用输出，数据长度：{1}")]
	public void KcpOutputBegin(uint connectionId, int length)
	{
		WriteEvent(7, connectionId, length);
	}

	[Event(8, Level = EventLevel.Verbose, Keywords = (EventKeywords)KcpEventKeywords.Internal)]
	public void KcpHeaderWasRead(uint connectionId, uint conv, uint cmd, uint frg, uint wnd, uint ts, uint sn, uint una, uint length)
	{
		WriteEvent(8, connectionId, conv, cmd, frg, wnd, ts, sn, una, length);
	}

	[Event(9, Level = EventLevel.Informational, Keywords = (EventKeywords)KcpEventKeywords.Internal)]
	public void KcpProbeReceived(uint connectionId)
	{
		WriteEvent(9, connectionId);
	}

	[Event(10, Level = EventLevel.Informational, Keywords = (EventKeywords)KcpEventKeywords.Internal, Message = "收到对等端的窗口告知信息")]
	public void KcpWindowTold(uint connectionId, uint wnd)
	{
		WriteEvent(10, connectionId, wnd);
	}

	[Event(11, Level = EventLevel.Verbose, Keywords = (EventKeywords)KcpEventKeywords.Internal)]
	public void KcpRemoteAckReceived(uint connectionId, uint sn, uint ts)
	{
		WriteEvent(11, connectionId, sn, ts);
	}

	[Event(12, Level = EventLevel.Verbose, Keywords = (EventKeywords)KcpEventKeywords.Internal)]
	public void KcpRemotePushReceived(uint connectionId, uint sn, uint ts, uint length)
	{
		WriteEvent(12, connectionId, sn, ts, length);
	}

	[Event(13, Level = EventLevel.Warning, Keywords = (EventKeywords)KcpEventKeywords.Internal)]
	public void KcpFlushBufferNotEnough(uint connectionId, uint mtu, uint sn, uint xmit, int encodedLength, int neededLength, int bufferSizeLimit)
	{
		WriteEvent(13, connectionId, mtu, sn, xmit, encodedLength, neededLength, bufferSizeLimit);
	}

	[Event(14, Level = EventLevel.Verbose, Keywords = (EventKeywords)(KcpEventKeywords.Internal | KcpEventKeywords.NeedSend),
		   Message = "连接 {0} 发出 sn[{2}] 数据包，长度 {1}。分片序号 [{3}]；剩余窗口 {4}；UNA {5}")]
	public void KcpEmittingSinglePushPacket(uint conv, int length, uint sn, byte frg, ushort wnd, uint una)
	{
		WriteEvent(14, conv, length, sn, frg, wnd, una);
	}

	[Event(15, Level = EventLevel.Error, Keywords = (EventKeywords)(KcpEventKeywords.Internal | KcpEventKeywords.DeadLink),
		   Message = "连接 {0} 达到最大重传次数 {2}，最后发送的包序号为 {1}。连接关闭")]
	internal void KcpDeadLink(uint conv, uint xmit, uint dead_link)
	{
		WriteEvent(15, conv, xmit, dead_link);
	}

	[Event(16, Level = EventLevel.Verbose, Keywords = (EventKeywords)KcpEventKeywords.Internal)]
	public void KcpCongestionWindowChange(uint connectionId, uint cwnd, uint ssthresh, uint incr)
	{
		WriteEvent(16, connectionId, cwnd, ssthresh, incr);
	}

	[Event(17, Level = EventLevel.Verbose, Keywords = (EventKeywords)KcpEventKeywords.Internal)]
	public void KcpFastRetransmit(uint connectionId, uint sn, uint fastack, uint resent)
	{
		WriteEvent(17, connectionId, sn, fastack, resent);
	}

	[Event(18, Level = EventLevel.Verbose, Keywords = (EventKeywords)KcpEventKeywords.Internal)]
	public void KcpRttUpdated(uint connectionId, int rtt, uint rto)
	{
		WriteEvent(18, connectionId, rtt, rto);
	}

	[Event(19, Level = EventLevel.Informational, Keywords = (EventKeywords)KcpEventKeywords.Internal,
		Message = "连接 {0} 窗口状态变化。是否已满：{1} (接收窗口 {2}，总分片数 {3}，有效窗口 {4})")]
	public void KcpWindowFullStateChange(uint connectionId, bool isFull, uint rcvWnd, uint totalPackets, uint effectiveWindow)
	{
		WriteEvent(19, connectionId, isFull, rcvWnd, totalPackets, effectiveWindow);
	}

	[Event(20, Level = EventLevel.Verbose, Keywords = (EventKeywords)KcpEventKeywords.Internal,
		Message = "连接 {0} 分片重组: SN={1}, 总分片数={2}, 当前分片={3}, 流模式={4}")]
	public void KcpFragmentReassembled(uint connectionId, uint sn, int totalFragments, byte currentFragment, bool isStreamMode)
	{
		WriteEvent(20, connectionId, sn, totalFragments, currentFragment, isStreamMode);
	}

	[Event(21, Level = EventLevel.Informational, Keywords = (EventKeywords)KcpEventKeywords.Api, Message = "连接 {0} 已被释放，最后发送的包序号为 {1}。连接关闭")]
	public void KcpConnectionDisposed(uint connectionId)
	{
		WriteEvent(21, connectionId);
	}

	[Event(22, Level = EventLevel.Informational, Keywords = (EventKeywords)KcpEventKeywords.Api)]
	public void KcpNoDelayChanged(uint conv, bool nodelay, int interval, int resend, bool nc)
	{
		WriteEvent(22, conv, nodelay, interval, resend, nc);
	}

	[Event(23, Level = EventLevel.Verbose, Keywords = (EventKeywords)(KcpEventKeywords.Internal | KcpEventKeywords.Buffer))]
	public void KcpBufferWasRent(int oldsize, int requiredSize, int actualCapacity)
	{
		WriteEvent(23, oldsize, requiredSize, actualCapacity);
	}

	[Event(24, Level = EventLevel.Verbose, Keywords = (EventKeywords)(KcpEventKeywords.Internal | KcpEventKeywords.Buffer))]
	public void KcpBufferWasReturned(int capacity)
	{
		WriteEvent(24, capacity);
	}

	[Event(25, Level = EventLevel.Verbose, Keywords = (EventKeywords)(KcpEventKeywords.Internal | KcpEventKeywords.Buffer))]
	public void KcpPacketBufferWantBuffer(uint connectionId, int requiredSize)
	{
		WriteEvent(25, connectionId, requiredSize);
	}

	[NonEvent]
	internal bool IsVerboseEnabled(KcpEventKeywords kw)
	{
		return IsEnabled(EventLevel.Verbose, (EventKeywords)kw);
	}
}