using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Text;

namespace FaGe.Kcp.Tracing;

[EventSource(Name = "FaGe.Kcp", Guid = "762347F4-61D5-4F4E-AA18-C3CA84F725B6")]
internal class KcpTraceEventSource : EventSource
{
	internal static readonly KcpTraceEventSource Log = new KcpTraceEventSource();

	[Flags]
	private enum KcpEventKeywords : long
	{
		None = 0x0,
		KcpInternal = 0x1,
		KcpApi = 0x2,
		KcpNeedSend = 0x4 & KcpInternal,
		KcpDeadLink = 0x8 & KcpInternal,
	}


	[Event(1, Level = EventLevel.Informational, Channel = EventChannel.Debug, Keywords = (EventKeywords)KcpEventKeywords.KcpApi)]
	public void KcpInputResult(int rawResult, uint connectionId)
	{
		WriteEvent(1, rawResult, connectionId);
	}

	[Event(2, Level = EventLevel.Informational, Channel = EventChannel.Debug, Keywords = (EventKeywords)KcpEventKeywords.KcpApi)]
	public void KcpReceiveFailed(int rawResult, uint connectionId)
	{
		WriteEvent(2, rawResult, connectionId);
	}

	[Event(3, Level = EventLevel.Informational, Channel = EventChannel.Debug, Keywords = (EventKeywords)KcpEventKeywords.KcpApi)]
	public void KcpUpdateBegin(uint connectionId, uint timeticks)
	{
		WriteEvent(3, connectionId, timeticks);
	}

	[Event(4, Level = EventLevel.Informational, Channel = EventChannel.Debug, Keywords = (EventKeywords)KcpEventKeywords.KcpApi)]
	public void KcpFlush(uint connectionId)
	{
		WriteEvent(4, connectionId);
	}

	[Event(5, Level = EventLevel.Informational, Channel = EventChannel.Debug, Keywords = (EventKeywords)KcpEventKeywords.KcpApi)]
	public void KcpSendEnd(uint connectionId, int length, int rawErrorCode)
	{
		WriteEvent(5, connectionId, length, rawErrorCode);
	}

	[Event(5, Level = EventLevel.Informational, Channel = EventChannel.Debug, Keywords = (EventKeywords)KcpEventKeywords.KcpApi, Message = "连接 {0} 尝试获取数据包，长度：{1}")]
	public void KcpReceived(uint connectionId, int length)
	{
		WriteEvent(6, connectionId, length);
	}

	[Event(6, Level = EventLevel.Informational, Channel = EventChannel.Debug, Keywords = (EventKeywords)KcpEventKeywords.KcpApi, Message = "连接 {0} 调用输出，数据长度：{1}")]
	public void KcpOutputBegin(uint connectionId, int length)
	{
		WriteEvent(7, connectionId, length);
	}

	[Event(7, Level = EventLevel.Informational, Channel = EventChannel.Debug, Keywords = (EventKeywords)KcpEventKeywords.KcpInternal)]
	public void KcpWriteHeader(uint connectionId, uint conv, uint cmd, uint frg, uint wnd, uint ts, uint sn, uint una, uint length)
	{
		WriteEvent(8, connectionId, conv, cmd, frg, wnd, ts, sn, una, length);
	}

	public void KcpProbeReceived(uint connectionId)
	{
		WriteEvent(9, connectionId);
	}

	[Event(10, Level = EventLevel.Informational, Channel = EventChannel.Debug, Keywords = (EventKeywords)KcpEventKeywords.KcpInternal, Message = "收到对等端的窗口告知信息")]
	public void KcpWindowTold(uint connectionId, uint wnd)
	{
		WriteEvent(10, connectionId, wnd);
	}

	public void KcpRemoteAckReceived(uint connectionId, uint sn, uint ts)
	{
		WriteEvent(11, connectionId, sn);
	}

	public void KcpRemotePushReceived(uint connectionId, uint sn, uint ts, uint length)
	{
		WriteEvent(12, connectionId, sn, length);
	}

	public void KcpFlushBufferNotEnough(uint connectionId, uint mtu, uint sn, uint xmit, int bufferLength, int neededLength)
	{
		WriteEvent(13, connectionId, sn, xmit, mtu, bufferLength, neededLength);
	}

	[Event(14, Channel = EventChannel.Debug, Keywords = (EventKeywords)KcpEventKeywords.KcpNeedSend, Message = "连接 {0} 发出 sn[{2}] 数据包，长度 {1}。分片序号 [{3}]；剩余窗口 {4}；UNA {5}")]
	public void KcpEmittingSinglePushPacket(uint conv, int length, uint sn, byte frg, ushort wnd, uint una)
	{
		WriteEvent(14, conv, length, sn, frg, wnd, una);
	}

	[Event(15, Channel = EventChannel.Debug, Keywords = (EventKeywords)KcpEventKeywords.KcpDeadLink, Message = "连接 {0} 达到最大重传次数 {2}，最后发送的包序号为 {1}。连接关闭")]
	internal void KcpDeadLink(uint conv, uint xmit, uint dead_link)
	{
		WriteEvent(15, conv, xmit, dead_link);
	}

	// 拥塞控制事件
	public void KcpCongestionWindowChange(uint connectionId, uint cwnd, uint ssthresh, uint incr)
	{
		WriteEvent(16, connectionId, cwnd, ssthresh, incr);
	}

	// 快速重传事件  
	public void KcpFastRetransmit(uint connectionId, uint sn, uint fastack, uint resent)
	{
		WriteEvent(17, connectionId, sn, fastack, resent);
	}

	// RTT/RTO更新事件
	public void KcpRttUpdated(uint connectionId, int rtt, uint rto)
	{
		WriteEvent(18, connectionId, rtt, rto);
	}

	// 窗口状态变化事件
	public void KcpWindowFullStateChange(uint connectionId, bool isFull, uint rcvWnd)
	{
		WriteEvent(19, connectionId, isFull, rcvWnd);
	}

	// 分片重组事件
	public void KcpFragmentReassembled(uint connectionId, uint sn, int fragmentCount, uint frg)
	{
		WriteEvent(20, connectionId, sn, fragmentCount, frg);
	}
}
