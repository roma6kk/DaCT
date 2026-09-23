using System.Globalization;
using OnlineStore.Shared;

namespace OnlineStore.Tests;

public sealed class ParallelProcessingTests
{
    private static readonly Dictionary<string, string> OrderData = new()
    {
        ["customerId"] = "1", ["productId"] = "1", ["quantity"] = "1"
    };

    [Fact]
    public async Task OneClient_RequestIsProcessed()
    {
        await using var server = new TestServer(TimeSpan.Zero);

        var response = await TestServer.SendAsync(server.Port, new Request("CreateOrder", OrderData));

        Assert.True(response.Success);
        Assert.Equal("OrderCreated", response.Event);
        Assert.Equal(4, server.Logic.Products.Single(product => product.Id == 1).Stock);
        Assert.Contains(server.Logs, line => line.Contains("Client 1 START"));
        Assert.Contains(server.Logs, line => line.Contains("Client 1 END"));
    }

    [Fact]
    public async Task TwoClients_BothRequestsAreProcessed()
    {
        await using var server = new TestServer(TimeSpan.Zero);

        var responses = await Task.WhenAll(
            TestServer.SendAsync(server.Port, new Request("GetProducts")),
            TestServer.SendAsync(server.Port, new Request("CreateOrder", OrderData)));

        Assert.All(responses, response => Assert.True(response.Success));
        Assert.Contains(server.Logs, line => line.Contains("Client 1 END"));
        Assert.Contains(server.Logs, line => line.Contains("Client 2 END"));
    }

    [Fact]
    public async Task TwoClients_AreProcessedInParallel()
    {
        await using var server = new TestServer();

        var first = TestServer.SendAsync(server.Port, new Request("CreateOrder", OrderData));
        await Task.Delay(TimeSpan.FromSeconds(1));
        var second = TestServer.SendAsync(server.Port, new Request("CreateOrder", OrderData));
        var responses = await Task.WhenAll(first, second);

        var firstStart = ParseTimestamp(server.Logs.First(line => line.Contains("Client 1 START")));
        var firstEnd = ParseTimestamp(server.Logs.First(line => line.Contains("Client 1 END")));
        var secondStart = ParseTimestamp(server.Logs.First(line => line.Contains("Client 2 START")));

        Assert.All(responses, response => Assert.True(response.Success));
        Assert.True((firstEnd - firstStart).TotalSeconds >= 10);
        Assert.True(secondStart < firstEnd);
    }

    private static DateTime ParseTimestamp(string line) =>
        DateTime.ParseExact(line[1..13], "HH:mm:ss.fff", CultureInfo.InvariantCulture);
}
