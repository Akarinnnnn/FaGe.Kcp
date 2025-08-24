namespace FaGe.Kcp
{
	public enum KcpRecvFailureReason : int
	{
		EmptyReceiveRing = -1,
		PacketIncomplete = -2,
		DestinationBufferNotEnough = -3
	}
}