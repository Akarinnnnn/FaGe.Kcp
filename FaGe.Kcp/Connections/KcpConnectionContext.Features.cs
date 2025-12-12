using FaGe.Kcp.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using System.Buffers;
using System.Collections;
using System.Net.Sockets;

namespace Kcp.Kestrel.Connections;

public partial class KcpConnectionContext : IFeatureCollection, IKcpDefaultsFeature, IKcpFeature
{
	private FeatureCollection features = null!;

	private void SetupSelfFeatures() => features = new(2)
	{
		[typeof(IKcpDefaultsFeature)] = this,
		[typeof(IKcpFeature)] = this
	};

	public object? this[Type key]
	{
		get => features[key];
		set => features[key] = value;
	}

	public override IFeatureCollection Features => features;

	public bool IsReadOnly => features.IsReadOnly;
	public int Revision => features.Revision;

	public Func<ReadOnlySequence<byte>, KcpConnectionContext, CancellationToken, ValueTask> OutputCallbackAsync { get; set; }

	public TFeature? Get<TFeature>()
	{
		return features.Get<TFeature>();
	}

	IEnumerator<KeyValuePair<Type, object>> IEnumerable<KeyValuePair<Type, object>>.GetEnumerator()
		=> features.GetEnumerator();

	public void Set<TFeature>(TFeature? instance)
		=> features.Set(instance);

	IEnumerator IEnumerable.GetEnumerator()
	{
		return ((IEnumerable<KeyValuePair<Type, object>>)this).GetEnumerator();
	}

	public void InitializeUdpOutput(UdpClient udpClient)
	{
		throw new NotImplementedException();
	}

	public void Update(uint kcpTickNow)
	{
		throw new NotImplementedException();
	}
}
