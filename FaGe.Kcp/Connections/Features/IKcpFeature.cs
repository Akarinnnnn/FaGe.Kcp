using Kcp.Kestrel.Connections;
using System.Buffers;

namespace FaGe.Kcp.Connections.Features;

public interface IKcpFeature
{
	Func<ReadOnlySequence<byte>, KcpConnectionContext,CancellationToken, ValueTask> OutputCallbackAsync { get; set; }

	void Update(uint kcpTickNow);
}