using System.Net.Sockets;

namespace FaGe.Kcp.Connections.Features;

public interface IKcpDefaultsFeature : IKcpFeature, IKcpConfigurationFeature
{
	void InitializeUdpOutput(UdpClient udpClient);
}