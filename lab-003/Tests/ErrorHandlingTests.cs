using System.Net;
using System.Net.Sockets;
using OnlineStore.Shared;

namespace OnlineStore.Tests;

public sealed class ErrorHandlingTests
{
    private static readonly Dictionary<string, string> OrderData = new()
    {
        ["customerId"] = "1", ["productId"] = "1", ["quantity"] = "1"
    };

    [Fact]
    public async Task ServerUnavailable_ClientReturnsError()
    {
        var response = await new OnlineStore.Client.Client(port: TestServer.GetFreePort())
            .SendAsync(new Request("GetProducts"));

        Assert.False(response.Success);
        Assert.Equal("Сервер недоступен", response.Message);
    }

    [Fact]
    public async Task InvalidRequest_ServerReturnsErrorAndStaysAlive()
    {
        await using var server = new TestServer(TimeSpan.Zero);

        var invalid = await TestServer.SendRawAsync(server.Port, "это не json");
        var valid = await TestServer.SendAsync(server.Port, new Request("GetProducts"));

        Assert.False(invalid.Success);
        Assert.True(valid.Success);
    }

    [Fact]
    public async Task MissingResource_ServerReturnsError()
    {
        await using var server = new TestServer(TimeSpan.Zero);

        var missingProduct = await TestServer.SendAsync(server.Port, new Request("CreateOrder", new Dictionary<string, string>
        {
            ["customerId"] = "1", ["productId"] = "999", ["quantity"] = "1"
        }));
        var missingOrder = await TestServer.SendAsync(server.Port, new Request("PayOrder", new Dictionary<string, string>
        {
            ["orderId"] = "999"
        }));

        Assert.False(missingProduct.Success);
        Assert.Equal("Товар не найден", missingProduct.Message);
        Assert.False(missingOrder.Success);
        Assert.Equal("Заказ не найден", missingOrder.Message);
    }

    [Fact]
    public async Task ClientDisconnectsDuringProcessing_ServerKeepsRunning()
    {
        await using var server = new TestServer();

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, server.Port);
            client.Client.LingerState = new LingerOption(true, 0);
            await using var stream = client.GetStream();
            await using var writer = new StreamWriter(stream) { AutoFlush = true };
            await writer.WriteLineAsync(JsonProtocol.Serialize(new Request("CreateOrder", OrderData)));
            await server.WaitForLogAsync(line => line.Contains("Client 1 START"), TimeSpan.FromSeconds(5));
        }

        var response = await TestServer.SendAsync(server.Port, new Request("GetProducts"));

        Assert.True(response.Success);
        Assert.True(await server.WaitForLogAsync(line => line.Contains("Client 1 END"), TimeSpan.FromSeconds(15)));

        Assert.True(await server.WaitForLogAsync(line => line.Contains("Client 1 ОТКЛЮЧЁН"), TimeSpan.FromSeconds(10)));
        Assert.Contains(server.Logs, line => line.Contains("Client 1 ОТКЛЮЧЁН"));
    }
}
