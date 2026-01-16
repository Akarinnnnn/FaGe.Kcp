// See https://aka.ms/new-console-template for more information
using FaGe.Kcp.Connections;
using System.Net;
using System.Net.Sockets;
using System.Text;

Console.WriteLine("Hello, World!");
UdpClient designatedClient = new UdpClient(50001);
designatedClient.Connect(new IPEndPoint(IPAddress.Loopback, 40001));
KcpConnection kcpConnection = new KcpConnection(designatedClient, 123);

CancellationTokenSource cts = new CancellationTokenSource();

Console.CancelKeyPress += Console_CancelKeyPress;

void Console_CancelKeyPress(object? sender, ConsoleCancelEventArgs e)
{
	cts.Cancel();
	kcpConnection.Dispose();
}

Memory<byte> sendBuffer = Encoding.UTF8.GetBytes("Test FaGe.Kcp Message");


var send = Task.Run(() =>
{
	var ct = cts.Token;
	while (!ct.IsCancellationRequested)
	{
		if (Console.ReadKey().Key == ConsoleKey.S)
		{
			// 发送数据
			var result = kcpConnection.async(sendBuffer, ct);
			Console.WriteLine("Test FaGe.Kcp Message"); 
		}
	}
});
var update = Task.Run(async () =>
{
	var ct = cts.Token;
	try
	{
		while (!ct.IsCancellationRequested)
		{

			// 使用.NET时钟更新KCP连接
			await kcpConnection.UpdateAsync((uint)Environment.TickCount, ct);
			await Task.Delay(10, ct);
		}
	}
	catch (Exception e)
	{
		Console.WriteLine("error:");
		Console.WriteLine(e);
		cts.Cancel();
	}
});

await Task.WhenAll(send, update);