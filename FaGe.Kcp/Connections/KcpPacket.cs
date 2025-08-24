#pragma warning disable IDE1006 // 命名样式

using System.Buffers;
using System.Diagnostics;

namespace FaGe.Kcp.Connections;

/// <summary>
/// KCP传输段
/// </summary>
/// <param name="resendts">重传的时间戳。超过当前时间重发这个包</param>
/// <param name="rto">超时重传时间，根据网络去定</param>
/// <param name="fastack">快速重传机制，记录被跳过的次数，超过次数进行快速重传</param>
/// <param name="xmit">重传次数</param>
/// <param name="Data">数据内容</param>
/// <param name="PacketHeader">传输包头</param>
internal readonly record struct KcpSegment(
	uint resendts, uint rto, uint fastack, uint xmit,
	ReadOnlyMemory<byte> Data, KcpPacketHeader PacketHeader)
{
	/// <summary>
	/// 转换为传输形式，并写入到目标
	/// </summary>
	/// <param name="destination">写入目标</param>
	/// <returns>写入数据量</returns>
	public KcpSegmentWriteResult WriteTransport(IBufferWriter<byte> destination)
	{
		PacketHeader.ToTransportForm().Write(destination);


	}
}


internal readonly record struct KcpSegmentWriteResult(OperationStatus Status, uint TotalFragments,
	ReadOnlyMemory<byte> RemainingData, KcpPacketHeader PreviousHeader)
{

	/// <summary>
	/// 转换为传输形式，并写入到目标
	/// </summary>
	/// <param name="destination">写入目标</param>
	/// <returns>写入数据量</returns>
	public KcpSegmentWriteResult ContniueWriteTransport(IBufferWriter<byte> destination)
	{
		Debug.Assert(PreviousHeader.ValueAnyEndian.cmd == KcpCommand.Push);
	}
}
