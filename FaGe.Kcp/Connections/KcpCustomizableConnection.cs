using System.Buffers;
using System.Diagnostics;

namespace FaGe.Kcp.Connections;

public sealed class KcpCustomizableConnection(uint conversationId, KcpConnectionOptionsBase? options = null) : KcpConnectionBase(conversationId, options)
{
	public Func<ReadOnlyMemory<byte>, KcpCustomizableConnection, CancellationToken, ValueTask>? OutputCallbackAsync { get; set; }

	protected override ValueTask InvokeOutputCallbackAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
	{
		Debug.Assert(OutputCallbackAsync != null);
		return OutputCallbackAsync(buffer, this, cancellationToken);
	}
}