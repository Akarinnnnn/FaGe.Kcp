using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks.Sources;

namespace FaGe.Kcp.Utility
{
	internal class ConvertResultValueTaskSource<T> : IValueTaskSource
	{
		private ValueTask<T> source;
		private ValueTaskAwaiter<T> sourceAwaiter;
		private ManualResetValueTaskSourceCore<T> core;

		private void SetResultFromSource()
		{
			core.SetResult(sourceAwaiter.GetResult());
		}

		public ValueTask Reset(ValueTask<T> newSource)
		{
			source = newSource;
			sourceAwaiter = newSource.GetAwaiter();
			sourceAwaiter.OnCompleted(SetResultFromSource);

			core.Reset();
			return new ValueTask(this, core.Version);
		}

		public void GetResult(short token)
		{
			core.GetResult(token);
		}

		public ValueTaskSourceStatus GetStatus(short token)
		{
			return core.GetStatus(token);
		}

		public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
		{
			core.OnCompleted(continuation, state, token, flags);
		}
	}

	internal class ConvertResultValueTaskSource<TSource, TTarget> : IValueTaskSource<TTarget>
	{
		private ValueTask<TSource> source;
		private ValueTaskAwaiter<TSource> sourceAwaiter;
		private ManualResetValueTaskSourceCore<TTarget> core;


		public required Func<TSource, TTarget> ConvertResult { get; init; }

		private void SetResultFromSource()
		{
			core.SetResult(ConvertResult(sourceAwaiter.GetResult()));
		}

		public ValueTask<TTarget> Reset(ValueTask<TSource> newSource)
		{
			source = newSource;
			sourceAwaiter = newSource.GetAwaiter();
			sourceAwaiter.OnCompleted(SetResultFromSource);
			
			core.Reset();
			return new ValueTask<TTarget>(this, core.Version);
		}

		public TTarget GetResult(short token)
		{
			return core.GetResult(token);
		}

		public ValueTaskSourceStatus GetStatus(short token)
		{
			return core.GetStatus(token);
		}

		public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
		{
			core.OnCompleted(continuation, state, token, flags);
		}
	}

	internal class ConvertResultVoidTaskSource<TTarget> : IValueTaskSource<TTarget>
	{
		private ValueTask source;
		private ValueTaskAwaiter sourceAwaiter;
		private ManualResetValueTaskSourceCore<TTarget> core;


		public required Func<TTarget> ConvertResult { get; init; }

		private void SetResultFromSource()
		{
			sourceAwaiter.GetResult();
			core.SetResult(ConvertResult());
		}

		public ValueTask<TTarget> Reset(ValueTask newSource)
		{
			source = newSource;
			sourceAwaiter = newSource.GetAwaiter();
			sourceAwaiter.OnCompleted(SetResultFromSource);
			
			core.Reset();
			return new ValueTask<TTarget>(this, core.Version);
		}

		public TTarget GetResult(short token)
		{
			return core.GetResult(token);
		}

		public ValueTaskSourceStatus GetStatus(short token)
		{
			return core.GetStatus(token);
		}

		public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
		{
			core.OnCompleted(continuation, state, token, flags);
		}
	}
}
