using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FaGe.Kcp;

[StructLayout(LayoutKind.Sequential)]
public struct KcpPacketHeaderAnyEndian
{
	/// <summary>
	/// conversation id
	/// </summary>
	/// <devdoc>
	/// <remarks>
	/// during the connection life-time.
	/// <para>
	/// It is represented by a 32 bits integer which is given at the moment the KCP
	/// control block(aka. struct ikcpcb, or kcp object) has been created.Each
	/// packet sent out will carry the conversation id in the first 4 bytes and a
	/// packet from remote endpoint will not be accepted if it has a different
	/// conversation id.
	/// </para>
	/// <para>
	/// The value can be any random number, but in practice, both side between a
	/// connection will have many KCP objects (or control block) storing in the
	/// containers like a map or an array.A index is used as the key to look up one
	/// KCP object from the container. 
	/// </para>
	/// <para>
	/// So, the higher 16 bits of conversation id can be used as caller's index while
	/// the lower 16 bits can be used as callee's index. KCP will not handle
	/// handshake, and the index in both side can be decided and exchanged after
	/// connection establish.
	/// </para>
	/// <para>
	/// When you receive and accept a remote packet, the local index can be extracted
	/// from the conversation id and the kcp object which is in charge of this
	/// connection can be find out from your map or array.
	/// </para>
	/// </remarks>
	/// </devdoc>
#pragma warning disable IDE1006 // 命名样式
	[EditorBrowsable(EditorBrowsableState.Advanced)]
	public uint conv { get; set; }
	[EditorBrowsable(EditorBrowsableState.Advanced)]
	public KcpCommand cmd { get; set; }
	[EditorBrowsable(EditorBrowsableState.Advanced)]
	public byte frg { get; set; }
	[EditorBrowsable(EditorBrowsableState.Advanced)]
	public ushort wnd { get; set; }
	[EditorBrowsable(EditorBrowsableState.Advanced)]
	public uint ts { get; set; }
	[EditorBrowsable(EditorBrowsableState.Advanced)]
	public uint sn { get; set; }
	[EditorBrowsable(EditorBrowsableState.Advanced)]
	public uint una { get; set; }
	[EditorBrowsable(EditorBrowsableState.Advanced)]
	public uint len { get; set; }
#pragma warning restore IDE1006 // 命名样式

	internal const int ExpectedSize = KcpConst.IKCP_OVERHEAD;

	/// <summary>
	/// 调整端序并产生新实例
	/// </summary>
	internal readonly KcpPacketHeaderAnyEndian ReverseEndianness()
	{
		var result = this;

		result.conv = BinaryPrimitives.ReverseEndianness(conv);
		// result.cmd result.frag 是 byte
		result.wnd = BinaryPrimitives.ReverseEndianness(wnd);
		result.ts = BinaryPrimitives.ReverseEndianness(ts);
		result.sn = BinaryPrimitives.ReverseEndianness(sn);
		result.una = BinaryPrimitives.ReverseEndianness(una);
		result.sn = BinaryPrimitives.ReverseEndianness(sn);

		return result;
	}

	internal static KcpPacketHeaderAnyEndian? Decode(ReadOnlySpan<byte> span)
	{
		if (span.Length < ExpectedSize)
			return null;

		return Unsafe.ReadUnaligned<KcpPacketHeaderAnyEndian>(ref MemoryMarshal.GetReference(span));
	}

	internal static KcpPacketHeader? DecodeToMachineForm(ReadOnlySpan<byte> span)
	{
		var headerAnyEndian = Decode(span);
		if (headerAnyEndian is null)
			return null;

		var header = new KcpPacketHeader(headerAnyEndian.Value.ReverseEndianness(), false);
		return header;
	}
}
