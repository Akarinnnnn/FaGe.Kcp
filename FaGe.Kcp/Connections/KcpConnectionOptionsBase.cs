using System.Buffers;

namespace FaGe.Kcp.Connections
{
	public class KcpConnectionOptionsBase
	{
		public ArrayPool<byte>? InternalPacketMemoryPool { get; set; }
	}
}