// See https://aka.ms/new-console-template for more information
using FaGe.Kcp.Connections;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Unicode;

Console.WriteLine("Hello, World!");
UdpClient designatedClient = new(50001);
var remoteEp = new IPEndPoint(IPAddress.Loopback, 40001);
KcpConnection kcpConnection = new KcpConnection(designatedClient, 2001, remoteEp);

CancellationTokenSource cts = new CancellationTokenSource();
ManualResetEventSlim exceptionStopEvent = new(false);

Console.CancelKeyPress += Console_CancelKeyPress;


void Console_CancelKeyPress(object? sender, ConsoleCancelEventArgs e)
{
	cts.Cancel();
	kcpConnection.Dispose();
	exceptionStopEvent.Wait();
}

Memory<byte> sendBuffer = Encoding.UTF8.GetBytes("发送一条消息");


var send = Task.Run(async () =>
{
	var ct = cts.Token;
	while (!ct.IsCancellationRequested)
	{
		if (Console.ReadKey().Key == ConsoleKey.S)
		{
			// 发送数据
			var result = await kcpConnection.SendAsync(sendBuffer, ct);
			Console.WriteLine("Test FaGe.Kcp Message");
			if (result.IsSucceed)
			{
				var packet = await kcpConnection.ReceiveAsync(ct);
				if (Utf8.IsValid(packet.Result.Buffer.FirstSpan))
				{
					Console.WriteLine(Encoding.UTF8.GetString(packet.Result.Buffer.FirstSpan));
				}
			}
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
			await Task.Delay(2000, ct);
		}
	}
	catch (Exception e)
	{
		Console.WriteLine("error:");
		Console.WriteLine(e);
		cts.Cancel();
		exceptionStopEvent.Set();
	}
});

await Task.WhenAll(send, update);