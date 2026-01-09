using System;
using System.Collections.Generic;
using System.Text;

namespace FaGe.Kcp
{
	public struct KcpInputResult
	{
		private readonly int resultValue;

		internal KcpInputResult(int rawResult)
		{
			resultValue = rawResult;
		}

		public readonly bool IsFailed => resultValue < 0;

		public readonly int RawResult => resultValue;

		public readonly int LengthInput
		{
			get
			{
				if (!IsFailed)
					return resultValue;

				throw new InvalidOperationException($"接收失败，请检查{nameof(RawResult)}");
			}
		}
	}
}