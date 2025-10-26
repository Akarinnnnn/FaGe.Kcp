using System.Diagnostics.CodeAnalysis;

namespace FaGe.Kcp.Connections;

public readonly record struct KcpSequenceSendResult(long? SentCount, KcpSendStatus FailReasoon)
{
	[MemberNotNullWhen(true, nameof(SentCount))]
	public bool IsSucceed => FailReasoon == KcpSendStatus.Succeed;

	public static KcpSequenceSendResult Succeed(long sentCount) => new(sentCount, KcpSendStatus.Succeed);
	public static KcpSequenceSendResult Fail(KcpSendStatus reason) => new(null, reason);
	
}

public readonly record struct KcpSendResult(int? SentCount, KcpSendStatus FailReasoon)
{
	[MemberNotNullWhen(true, nameof(SentCount))]
	public bool IsSucceed => FailReasoon == KcpSendStatus.Succeed;

	public static KcpSendResult Succeed(int sentCount) => new(sentCount, KcpSendStatus.Succeed);
	public static KcpSendResult Fail(KcpSendStatus reason) => new(null, reason);

	public static implicit operator KcpSequenceSendResult(KcpSendResult value)
	{
		return new(value.SentCount ?? null, value.FailReasoon);
	}
}