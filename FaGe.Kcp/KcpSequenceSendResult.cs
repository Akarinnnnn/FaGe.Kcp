using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace FaGe.Kcp;

public readonly record struct KcpSequenceSendResult(long? SentCount, KcpSendStatus FailReasoon)
{
	[MemberNotNullWhen(true, nameof(SentCount))]
	public bool IsSucceed => FailReasoon == KcpSendStatus.Succeed;

	public static KcpSequenceSendResult Succeed(long sentCount) => new(sentCount, KcpSendStatus.Succeed);
	public static KcpSequenceSendResult Fail(KcpSendStatus reason) => new(null, reason);
	
}

public readonly record struct KcpSendResult(int? SentCount, KcpSendStatus FailReason)
{
	[MemberNotNullWhen(true, nameof(SentCount))]
	public bool IsSucceed => FailReason == KcpSendStatus.Succeed;

	internal readonly Task? asyncSendTask;

	internal KcpSendResult(int? sentCount, KcpSendStatus failReason, Task asyncSendTask)
		: this(sentCount, failReason)
	{
		Debug.Assert(asyncSendTask != null, "This constructor is served for async scenario.");
		this.asyncSendTask = asyncSendTask;
	}

	internal static KcpSendResult Succeed(int sentCount, TaskCompletionSource asyncSource) => new(sentCount, KcpSendStatus.Succeed, asyncSource.Task);
	public static KcpSendResult Succeed(int sentCount) => new(sentCount, KcpSendStatus.Succeed);

	public static KcpSendResult Fail(KcpSendStatus reason) => new(null, reason);

	public static implicit operator KcpSequenceSendResult(KcpSendResult value)
	{
		return new(value.SentCount ?? null, value.FailReason);
	}
}