using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FaGe.Kcp;

public readonly struct KcpRecvResult
{
	private readonly int resultValue;

	internal KcpRecvResult(int rawResult)
	{
		resultValue = rawResult;
	}

	public readonly bool IsFailed => resultValue < 0;

	public readonly KcpRecvFailureReason FailureReason
	{
		get
		{
			if (IsFailed)
				return (KcpRecvFailureReason)resultValue;

			throw new InvalidOperationException("接收成功，没有失败");
		}
	}

	public readonly int ReceivedLength
	{
		get
		{
			if (!IsFailed)
				return resultValue;

			throw new InvalidOperationException($"接收失败，请检查{nameof(FailureReason)}");
		}
	}
}
