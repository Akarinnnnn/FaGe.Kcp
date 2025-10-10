using FaGe.Kcp;

namespace FaGe.Kcp.Connections
{
	[Flags]
	public enum AskType
	{
		/// <summary>
		/// 告知远端窗口大小
		/// </summary>
		Tell = KcpConst.IKCP_ASK_TELL,
		/// <summary>
		/// 请求远端告知窗口大小
		/// </summary>
		Send = KcpConst.IKCP_ASK_SEND,
	}
}