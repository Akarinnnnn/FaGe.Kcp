using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FaGe.Kcp;

public readonly record struct KcpPacketHeader(KcpPacketHeaderAnyEndian ValueAnyEndian, bool IsTransportEndian)
{
	public KcpPacketHeader ToTransportForm()
	{
		if (!IsTransportEndian && BitConverter.IsLittleEndian) // 改大端要改这里
		{
			var machineEndian = ValueAnyEndian;
			machineEndian.AdjustEndianness();
			return new(machineEndian, true);
		}
		else
		{
			return this;
		}
	}

	public KcpPacketHeader MachineForm
	{
		get
		{
			if (IsTransportEndian && BitConverter.IsLittleEndian)
			{
				var transportEndian = ValueAnyEndian;
				transportEndian.AdjustEndianness();
				return new(transportEndian, true);
			}
			else
			{
				return this;
			}
		}
	}

	internal void Write(Span<byte> dstSpan)
	{
		var transportEndianCopy = ToTransportForm().ValueAnyEndian;

#if DEBUG
		var debugHeaderSpan = MemoryMarshal.AsBytes<KcpPacketHeaderAnyEndian>(new(ref transportEndianCopy));
		Debug.Assert(debugHeaderSpan.Length == KcpPacketHeaderAnyEndian.ExpectedSize);
		debugHeaderSpan.CopyTo(dstSpan);
#else
		MemoryMarshal.AsBytes<KcpPacketHeaderAnyEndian>(new(ref transportEndianCopy)).CopyTo(dstSpan);
#endif
	}

	internal static bool TryRead(ReadOnlySequence<byte> data, out KcpPacketHeader header)
	{
		header = default;
		bool result;

		SequenceReader<byte> reader = new(data);

		KcpPacketHeaderAnyEndian transportEndian = default;
		Span<byte> dstSpan = MemoryMarshal.AsBytes(new Span<KcpPacketHeaderAnyEndian>(ref transportEndian));

		Debug.Assert(dstSpan.Length == KcpPacketHeaderAnyEndian.ExpectedSize);

		result = reader.TryCopyTo(dstSpan);

		if (result)
		{
			header = new(transportEndian.AdjustEndianness(), false);
		}

		return result;
	}

	internal static bool TryRead(ReadOnlySpan<byte> data, out KcpPacketHeader header)
	{
		header = default;

		KcpPacketHeaderAnyEndian headerAnyEndian = default;
		Span<byte> dstSpan = MemoryMarshal.AsBytes(new Span<KcpPacketHeaderAnyEndian>(ref headerAnyEndian));
		Debug.Assert(dstSpan.Length == KcpPacketHeaderAnyEndian.ExpectedSize);

		if (data.Length >= dstSpan.Length)
		{
			data[dstSpan.Length..].CopyTo(dstSpan);
			header = new(headerAnyEndian.AdjustEndianness(), false);
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
