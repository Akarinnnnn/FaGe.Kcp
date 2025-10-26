namespace FaGe.Kcp
{
	public enum KcpSendStatus : int
	{
        Succeed = 0,
		EmptyBuffer = -1,
		/// <summary>
		/// 等效于EAGAIN
		/// </summary>
		TryAgainLater = -2,
		DestinationBufferNotEnough = -3
	}
}