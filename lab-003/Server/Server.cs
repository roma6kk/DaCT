using System.Net;
using System.Net.Sockets;
using OnlineStore.Shared;

namespace OnlineStore.Server;

public sealed class Server(int port = 5000, IRequestProcessor? requestProcessor = null, Action<string>? log = null, TimeSpan? timeResponseDelay = null)
{
    private readonly Action<string> log = log ?? Console.WriteLine;
    private readonly IRequestProcessor requestProcessor = requestProcessor ?? new BusinessLogic(timeResponseDelay: timeResponseDelay, log: log ?? Console.WriteLine);
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
                _ = Task.Run(() => new RequestHandler(requestProcessor, clientId, log).HandleAsync(client));
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