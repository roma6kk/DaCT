using System.Net;
using System.Net.Sockets;

namespace OnlineStore.Server;

public sealed class Server(int port = 5000)
{
    private readonly BusinessLogic businessLogic = new();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.WriteLine($"Сервер запущен: 127.0.0.1:{port}");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = new RequestHandler(businessLogic).HandleAsync(client);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            listener.Stop();
        }
    }
}
