using System.Buffers;
using System.Diagnostics;

namespace FaGe.Kcp.Connections;

public sealed class KcpCustomizableConnection : KcpConnectionBase
{
	public KcpCustomizableConnection(uint conversationId) : base(conversationId)
	{
	}

	public Func<ReadOnlyMemory<byte>, KcpCustomizableConnection, CancellationToken, ValueTask>? OutputCallbackAsync { get; set; }

	protected sealed override ValueTask InvokeOutputCallbackAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
	{
		Debug.Assert(OutputCallbackAsync != null);
		return OutputCallbackAsync(buffer, this, cancellationToken);
	}

	public void ExternalUpdate(uint timeTickNow) => Update(timeTickNow);
}
