
namespace FaGe.Kcp
{
	[Serializable]
	public class KcpInputException : Exception
	{
		public KcpInputException()
		{
		}

		public KcpInputException(string? message, int errerCodeRaw) : base(message)
		{
			ErrerCodeRaw = errerCodeRaw;
		}

		public KcpInputException(string? message, int errorCodeRaw, Exception? innerException) : base(message, innerException)
		{
			ErrerCodeRaw = errorCodeRaw;
		}

		public int ErrerCodeRaw { get; }
	}
}