using FaGe.Kcp.Connections;
using System.IO.Pipelines;

namespace FaGe.Kcp;

#pragma warning disable IDE0250 // 将结构设置为 “readonly”
public struct KcpApplicationPacket(ReadResult result, KcpConnectionBase source, int packetFragmentCount) : IDisposable
#pragma warning restore IDE0250 // 此结构可能会修改source，因此不应将其设置为 “readonly”
{
	public readonly ReadResult Result = result;
	public readonly bool IsValid => source != null;
	public readonly bool IsInvalid => source == null;

	private KcpConnectionBase? source = source;
	private readonly int packetFragmentsCount = packetFragmentCount;

	public void ConsumeThisPacket() => Dispose();

	public void Advance() => Dispose();

	public void Dispose()
	{
		source?.AdvanceFragment(packetFragmentsCount);
		source = null;
	}
}