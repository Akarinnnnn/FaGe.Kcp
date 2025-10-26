using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;

namespace FaGe.Kcp.Utility;

internal class ConvertValueToVoidValueTaskSource<T> : IValueTaskSource
{
	private ValueTaskAwaiter<T> sourceAwaiter;
	private ManualResetValueTaskSourceCore<bool> core;

	private static readonly Action<T> s_noopConverter = _ => { };

	private Action<T> convertResult = s_noopConverter;

	private void SetResultFromSource()
	{
		try
		{
			var result = sourceAwaiter.GetResult();
			convertResult(result);
			core.SetResult(true);
		}
		catch (Exception e)
		{
			core.SetException(e);
		}
	}

	public ValueTask BeginAnotherConvert(ValueTask<T> newSource, Action<T>? convertResult = null)
	{
		sourceAwaiter = newSource.GetAwaiter();
		convertResult = convertResult ?? s_noopConverter;

		if (sourceAwaiter.IsCompleted)
			SetResultFromSource();
		else
			sourceAwaiter.OnCompleted(SetResultFromSource);

		core.Reset();
		return new ValueTask(this, core.Version);
	}

	void IValueTaskSource.GetResult(short token)
	{
		core.GetResult(token);
	}

	ValueTaskSourceStatus IValueTaskSource.GetStatus(short token)
	{
		return core.GetStatus(token);
	}

	void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
	{
		core.OnCompleted(continuation, state, token, flags);
	}
}

internal class ConvertValueToValueTaskSource<TSource, TTarget> : IValueTaskSource<TTarget>
{
	private ValueTaskAwaiter<TSource> sourceAwaiter;
	private ManualResetValueTaskSourceCore<TTarget> core;

	private Func<TSource, TTarget> convertResult = null!;

	private void SetResultFromSource()
	{
		try
		{
			core.SetResult(convertResult(sourceAwaiter.GetResult()));
		}
		catch (Exception e)
		{
			core.SetException(e);
		}
	}

	[MemberNotNull(nameof(convertResult))]
	public ValueTask<TTarget> BeginAnotherConvert(ValueTask<TSource> newSource, Func<TSource, TTarget> convertResult)
	{
		sourceAwaiter = newSource.GetAwaiter();
		this.convertResult = convertResult;

		if (!sourceAwaiter.IsCompleted)
			sourceAwaiter.OnCompleted(SetResultFromSource);
		else
			SetResultFromSource();

		core.Reset();
		return new ValueTask<TTarget>(this, core.Version);
	}

	TTarget IValueTaskSource<TTarget>.GetResult(short token)
	{
		return core.GetResult(token);
	}

	ValueTaskSourceStatus IValueTaskSource<TTarget>.GetStatus(short token)
	{
		return core.GetStatus(token);
	}

	void IValueTaskSource<TTarget>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
	{
		core.OnCompleted(continuation, state, token, flags);
	}
}

internal class ConvertVoidToResultTaskSource<TTarget> : IValueTaskSource<TTarget>
{
	private ValueTaskAwaiter sourceAwaiter;
	private ManualResetValueTaskSourceCore<TTarget> core;


	private Func<TTarget> convertResult = null!;

	private void SetResultFromSource()
	{
		try
		{
			sourceAwaiter.GetResult();
			core.SetResult(convertResult());
		}
		catch (Exception e)
		{
			core.SetException(e);
		}
	}

	public ValueTask<TTarget> BeginAnotherConvert(ValueTask newSource, Func<TTarget> convertResult)
	{
		sourceAwaiter = newSource.GetAwaiter();
		this.convertResult = convertResult;

		if (!sourceAwaiter.IsCompleted)
			sourceAwaiter.OnCompleted(SetResultFromSource);
		else
			SetResultFromSource();

		core.Reset();
		return new ValueTask<TTarget>(this, core.Version);
	}

	TTarget IValueTaskSource<TTarget>.GetResult(short token)
	{
		return core.GetResult(token);
	}

	ValueTaskSourceStatus IValueTaskSource<TTarget>.GetStatus(short token)
	{
		return core.GetStatus(token);
	}

	void IValueTaskSource<TTarget>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
	{
		core.OnCompleted(continuation, state, token, flags);
	}
}

internal static class ConvertValueTaskResultExtensions
{
	public static (ValueTask Task, ConvertValueToVoidValueTaskSource<T> TaskSource) ResultToVoid<T>(this ValueTask<T> source, Action<T>? convertResult)
	{
		var taskSource = new ConvertValueToVoidValueTaskSource<T>();
		return (taskSource.BeginAnotherConvert(source, convertResult), taskSource);
	}

	public static (ValueTask<TTarget> Task, ConvertValueToValueTaskSource<TSource, TTarget> TaskSource) ResultToResult<TSource, TTarget>(this ValueTask<TSource> source, Func<TSource, TTarget> convertResult)
	{
		var taskSource = new ConvertValueToValueTaskSource<TSource, TTarget>();
		return (taskSource.BeginAnotherConvert(source, convertResult), taskSource);
	}

	public static (ValueTask<TTarget> Task, ConvertVoidToResultTaskSource<TTarget> TaskSource) VoidToResult<TTarget>(this ValueTask source, Func<TTarget> convertResult)
	{
		var taskSource = new ConvertVoidToResultTaskSource<TTarget>();
		return (taskSource.BeginAnotherConvert(source, convertResult), taskSource);
	}
}