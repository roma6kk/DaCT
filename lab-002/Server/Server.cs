using System.Net;
using System.Net.Sockets;

namespace OnlineStore.Server;

public sealed class Server(int port = 5000, BusinessLogic? businessLogic = null, Action<string>? log = null)
{
    private readonly BusinessLogic businessLogic = businessLogic ?? new();
    private readonly Action<string> log = log ?? Console.WriteLine;
    private int clientCounter;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        log($"Сервер запущен: 127.0.0.1:{port}");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                var clientId = Interlocked.Increment(ref clientCounter);
                _ = Task.Run(() => new RequestHandler(businessLogic, clientId, log).HandleAsync(client));
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
