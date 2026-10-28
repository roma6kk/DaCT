using System.Net;
using System.Net.Sockets;
using Npgsql;
using OnlineStore.Server;
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
    public async Task DatabaseUnavailable_ServerReturnsErrorAndStaysAlive()
    {
        var brokenLogic = new BusinessLogic(
            "Host=127.0.0.1;Port=1;Database=missing;Username=x;Password=x",
            TimeSpan.Zero, log: _ => { });
        await using var server = new TestServer(logic: brokenLogic);

        var first = await TestServer.SendAsync(server.Port, new Request("GetProducts"));
        var second = await TestServer.SendAsync(server.Port, new Request("GetState"));

        Assert.False(first.Success);
        Assert.False(second.Success);
        Assert.Contains("Ошибка базы данных", first.Message); 
    }

    [Fact]
    public async Task InvalidRequest_ServerReturnsErrorAndStaysAlive()
    {
        await using var server = new TestServer();

        var invalid = await TestServer.SendRawAsync(server.Port, "это не json");
        var valid = await TestServer.SendAsync(server.Port, new Request("GetProducts"));

        Assert.False(invalid.Success);
        Assert.True(valid.Success);
    }

    [Fact]
    public async Task UnknownOperation_ReturnsError()
    {
        await using var server = new TestServer();

        var response = await TestServer.SendAsync(server.Port, new Request("NoSuchOperation"));

        Assert.False(response.Success);
        Assert.Equal("Неизвестная операция", response.Message);
    }

    [Fact]
    public async Task EmptyRequest_ReturnsError()
    {
        await using var server = new TestServer();

        var response = await TestServer.SendAsync(server.Port, new Request(""));

        Assert.False(response.Success);
        Assert.Equal("Некорректный запрос", response.Message);
    }

    [Fact]
    public async Task MissingResource_ReturnsError()
    {
        DbFixture.Reset();
        await using var server = new TestServer();

        var missingProduct = await TestServer.SendAsync(server.Port, new Request("CreateOrder",
            new Dictionary<string, string> { ["customerId"] = "1", ["productId"] = "999", ["quantity"] = "1" }));
        var missingOrder = await TestServer.SendAsync(server.Port, new Request("PayOrder",
            new Dictionary<string, string> { ["orderId"] = "999" }));

        Assert.False(missingProduct.Success);
        Assert.Equal("Товар не найден", missingProduct.Message);
        Assert.False(missingOrder.Success);
        Assert.Equal("Заказ не найден", missingOrder.Message);
    }

    [Fact]
    public async Task InvalidParameters_ReturnError()
    {
        DbFixture.Reset();
        await using var server = new TestServer();

        var badData = await TestServer.SendAsync(server.Port, new Request("CreateOrder",
            new Dictionary<string, string> { ["customerId"] = "abc", ["productId"] = "1", ["quantity"] = "1" }));
        var missingCustomer = await TestServer.SendAsync(server.Port, new Request("CreateOrder",
            new Dictionary<string, string> { ["customerId"] = "777", ["productId"] = "1", ["quantity"] = "1" }));
        var busyResource = await TestServer.SendAsync(server.Port, new Request("CreateOrder",
            new Dictionary<string, string> { ["customerId"] = "1", ["productId"] = "4", ["quantity"] = "99" }));

        Assert.False(badData.Success);
        Assert.Equal("Некорректные входные данные", badData.Message);
        Assert.False(missingCustomer.Success);
        Assert.Equal("Покупатель не найден", missingCustomer.Message);
        Assert.False(busyResource.Success);
        Assert.Equal("Недостаточно товара на складе", busyResource.Message);
        Assert.Equal(1, DbFixture.Stock(4)); 
    }

    [Fact]
    public void CheckConstraint_RejectsInvalidData()
    {
        DbFixture.Reset();

        var exception = Assert.ThrowsAny<NpgsqlException>(() =>
            DbFixture.Execute("UPDATE products SET stock = -1 WHERE id = 1"));

        Assert.Contains("check constraint", exception.Message);
        Assert.Equal(5, DbFixture.Stock(1)); 
    }

    [Fact]
    public async Task ClientDisconnectsDuringProcessing_ServerKeepsRunning()
    {
        DbFixture.Reset();
        await using var server = new TestServer(TimeSpan.FromSeconds(2));

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
