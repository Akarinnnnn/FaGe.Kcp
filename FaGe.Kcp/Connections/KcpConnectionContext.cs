using Microsoft.AspNetCore.Connections;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net.Sockets;

namespace Kcp.Kestrel.Connections;

public partial class KcpConnectionContext : ConnectionContext
{
	private long connectionStartedTick;
	private UdpClient? udpTransport;

	private KcpConnectionContext()
	{
		Items = new ConnectionItems();
		SetupSelfFeatures();
	}

	public override IDictionary<object, object?> Items { get; set; }

	public uint ConnectionConv { get; private set; }

	public override string ConnectionId { get; set; } = null!;

	public override IDuplexPipe Transport { get; set; } = null!;

}
