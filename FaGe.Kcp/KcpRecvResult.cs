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

	public readonly int RawResult => resultValue;

	public readonly int LengthReceived
	{
		get
		{
			if (!IsFailed)
				return resultValue;

			throw new InvalidOperationException($"接收失败，请检查{nameof(RawResult)}");
		}
	}
}
