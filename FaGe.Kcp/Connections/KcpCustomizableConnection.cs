using System.Buffers;
using System.Diagnostics;

namespace FaGe.Kcp.Connections;

public sealed class KcpCustomizableConnection : KcpConnectionBase
{
	public Func<ReadOnlySequence<byte>, KcpCustomizableConnection, CancellationToken, ValueTask>? OutputCallbackAsync { get; set; }

	private protected sealed override ValueTask InvokeOutputCallbackAsync(ReadOnlySequence<byte> buffer, CancellationToken cancellationToken)
	{
		Debug.Assert(OutputCallbackAsync != null);
		return OutputCallbackAsync(buffer, this, cancellationToken);
	}

	public void ExternalUpdate(uint timeTickNow) => BaseUpdate(timeTickNow);
}
