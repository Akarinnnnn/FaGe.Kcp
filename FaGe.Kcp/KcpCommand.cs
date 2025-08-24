namespace FaGe.Kcp;

public enum KcpCommand : byte
{
	Push = 81,
	Ack = 82,
	WindowProbe = 83,
	WindowSizeResponse = 84
}

internal enum KcpCommandU4 : uint
{
	Push = 81,
	Ack = 82,
	WindowProbe = 83,
	WindowSizeResponse = 84
}