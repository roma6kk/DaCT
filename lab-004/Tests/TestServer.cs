using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using OnlineStore.Server;
using OnlineStore.Shared;

namespace OnlineStore.Tests;

internal sealed class TestServer : IAsyncDisposable
{
    private readonly ConcurrentQueue<string> logs = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task serverTask;

    public TestServer(TimeSpan? processingDelay = null, TimeSpan? timeResponseDelay = null,
        IRequestProcessor? logic = null, TimeSpan? raceWindow = null)
    {
        DbFixture.EnsureInitialized();
        Port = GetFreePort();
        Logic = logic ?? new BusinessLogic(
            DbFixture.ConnectionString, processingDelay ?? TimeSpan.Zero, timeResponseDelay, raceWindow, logs.Enqueue);
        serverTask = new OnlineStore.Server.Server(Port, Logic, logs.Enqueue, connectionString: DbFixture.ConnectionString)
            .RunAsync(cancellation.Token);
    }

    public int Port { get; }

    public IRequestProcessor Logic { get; }

    
    public Action<string> LogSink => logs.Enqueue;

    public IReadOnlyList<string> Logs => logs.ToArray();

    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel();
        await serverTask;
    }

    public static async Task<Response> SendAsync(int port, Request request) =>
        await SendRawAsync(port, JsonProtocol.Serialize(request));

    public static async Task<Response> SendRawAsync(int port, string payload)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream);
        using var writer = new StreamWriter(stream) { AutoFlush = true };
        await writer.WriteLineAsync(payload);
        return JsonProtocol.Deserialize<Response>(await reader.ReadLineAsync() ?? "")
            ?? new(false, "Пустой ответ сервера");
    }

    public async Task<bool> WaitForLogAsync(Func<string, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !Logs.Any(predicate))
            await Task.Delay(100);
        return Logs.Any(predicate);
    }

    public static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
