using System.Net;
using System.Net.Sockets;
using OnlineStore.Shared;

namespace OnlineStore.Tests;

public sealed class ServerAvailabilityTests
{
    private static int GetFreePort()
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

    private static async Task<Response> SendRequestAsync(int port, Request request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream);
        using var writer = new StreamWriter(stream) { AutoFlush = true };
        await writer.WriteLineAsync(JsonProtocol.Serialize(request));
        return JsonProtocol.Deserialize<Response>(await reader.ReadLineAsync() ?? "")
            ?? new(false, "Пустой ответ сервера");
    }

    [Fact]
    public async Task Server_IsReachable_AcceptsRequest()
    {
        var port = GetFreePort();
        using var cts = new CancellationTokenSource();
        var serverTask = new OnlineStore.Server.Server(port).RunAsync(cts.Token);

        try
        {
            var response = await SendRequestAsync(port, new Request("GetProducts"));

            Assert.True(response.Success);
            Assert.NotNull(response.Data);
        }
        finally
        {
            cts.Cancel();
            await serverTask;
        }
    }
}