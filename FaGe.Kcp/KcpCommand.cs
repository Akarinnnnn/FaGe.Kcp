using static FaGe.Kcp.KcpConst;
namespace FaGe.Kcp;

public enum KcpCommand : byte
{
	Push = IKCP_CMD_PUSH,
	Ack = IKCP_CMD_ACK,
	WindowProbe = IKCP_CMD_WASK,
	WindowSizeTell = IKCP_CMD_WINS
}

internal enum KcpCommandU4 : uint
{
	Push = IKCP_CMD_PUSH,
	Ack = IKCP_CMD_ACK,
	WindowProbe = IKCP_CMD_WASK,
	WindowSizeResponse = IKCP_CMD_WINS
}