using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FaGe.Kcp;

public struct KcpPacketHeader(KcpPacketHeaderAnyEndian anyEndian, bool isTransport)
{
	/// <summary>
	/// 可修改字段的数据包头部。
	/// </summary>
	/// <remarks>
	/// 可能是机器端序，也可能是传输端序，取决于<see cref="IsTransportEndian"/>。特别注意不要直接对其赋值，以免端序错误。
	/// </remarks>
	public KcpPacketHeaderAnyEndian ValueAnyEndian = anyEndian;
	public readonly bool IsTransportEndian = isTransport;

	public readonly bool IsMachineEndian => !IsTransportEndian;

	public readonly KcpPacketHeader ToTransportForm()
	{
		if (!IsTransportEndian && !BitConverter.IsLittleEndian) // 改大端要改这里
		{
			var machineEndian = ValueAnyEndian;
			return new(machineEndian.ReverseEndianness(), true);
		}
		else
		{
			return this;
		}
	}

	public readonly KcpPacketHeader MachineForm
	{
		get
		{
			if (!IsMachineEndian && !BitConverter.IsLittleEndian) // 改大端要改这里
			{
				var transportEndian = ValueAnyEndian;
				return new(transportEndian.ReverseEndianness(), false);
			}
			else
			{
				return this;
			}
		}
	}

	public static KcpPacketHeader FromMachine(KcpPacketHeaderAnyEndian value) => new(value, false);
	internal static KcpPacketHeader FromTransport(KcpPacketHeaderAnyEndian value) => new(value, true);

	internal void Write(ref Span<byte> dstSpan)
	{
		var transportEndianCopy = ToTransportForm().ValueAnyEndian;

#if DEBUG
		var debugHeaderSpan = MemoryMarshal.AsBytes<KcpPacketHeaderAnyEndian>(new(ref transportEndianCopy));
		Debug.Assert(debugHeaderSpan.Length == KcpPacketHeaderAnyEndian.ExpectedSize);
		debugHeaderSpan.CopyTo(dstSpan);
#else
		MemoryMarshal.AsBytes<KcpPacketHeaderAnyEndian>(new(ref transportEndianCopy)).CopyTo(dstSpan);
#endif

		dstSpan = dstSpan[KcpPacketHeaderAnyEndian.ExpectedSize..];
	}

	internal static bool TryRead(ReadOnlySpan<byte> data, out KcpPacketHeader header)
	{
		header = default;

		KcpPacketHeaderAnyEndian headerAnyEndian = default;
		Span<byte> dstSpan = MemoryMarshal.AsBytes(new Span<KcpPacketHeaderAnyEndian>(ref headerAnyEndian));
		Debug.Assert(dstSpan.Length == KcpPacketHeaderAnyEndian.ExpectedSize);

		if (data.Length >= dstSpan.Length)
		{
			data[..dstSpan.Length].CopyTo(dstSpan);
			header = new(headerAnyEndian.ReverseEndianness(), false);
			return true;
		}
		else
		{
			return false;
		}
	}

	public KcpPacketHeader WithLength(int packetLength)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(packetLength);

		var value = MachineForm.ValueAnyEndian;
		value.len = (uint)packetLength;

		return new(value, false);
	}
}