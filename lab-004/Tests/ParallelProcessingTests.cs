using System.Globalization;
using OnlineStore.Shared;

namespace OnlineStore.Tests;


public sealed class ParallelProcessingTests
{
    private static readonly Dictionary<string, string> FirstOrder = new()
    {
        ["customerId"] = "1", ["productId"] = "1", ["quantity"] = "1"
    };

    private static readonly Dictionary<string, string> SecondOrder = new()
    {
        ["customerId"] = "1", ["productId"] = "2", ["quantity"] = "1"
    };

    [Fact]
    public async Task OneClient_RequestIsProcessed()
    {
        DbFixture.Reset();
        await using var server = new TestServer();

        var response = await TestServer.SendAsync(server.Port, new Request("CreateOrder", FirstOrder));

        Assert.True(response.Success);
        Assert.Equal("OrderCreated", response.Event);
        Assert.Equal(4, DbFixture.Stock(1));
        Assert.Contains(server.Logs, line => line.Contains("Client 1 START"));
        Assert.Contains(server.Logs, line => line.Contains("Client 1 END"));
    }

    [Fact]
    public async Task TwoClients_BothRequestsAreProcessed()
    {
        DbFixture.Reset();
        await using var server = new TestServer();

        var responses = await Task.WhenAll(
            TestServer.SendAsync(server.Port, new Request("GetProducts")),
            TestServer.SendAsync(server.Port, new Request("CreateOrder", SecondOrder)));

        Assert.All(responses, response => Assert.True(response.Success));
        Assert.Contains(server.Logs, line => line.Contains("Client 1 END"));
        Assert.Contains(server.Logs, line => line.Contains("Client 2 END"));
        Assert.Equal(7, DbFixture.Stock(2));
    }

    [Fact]
    public async Task TwoClients_AreProcessedInParallel()
    {
        DbFixture.Reset();
        
        await using var server = new TestServer(TimeSpan.FromSeconds(2));

        var first = TestServer.SendAsync(server.Port, new Request("CreateOrder", FirstOrder));
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        var second = TestServer.SendAsync(server.Port, new Request("CreateOrder", SecondOrder));
        var responses = await Task.WhenAll(first, second);

        Assert.All(responses, response => Assert.True(response.Success));

        var firstStart = ParseTimestamp(server.Logs.First(line => line.Contains("Client 1 START")));
        var firstEnd = ParseTimestamp(server.Logs.First(line => line.Contains("Client 1 END")));
        var secondStart = ParseTimestamp(server.Logs.First(line => line.Contains("Client 2 START")));

        Assert.True((firstEnd - firstStart).TotalSeconds >= 2);
        Assert.True(secondStart < firstEnd); 
    }

    private static DateTime ParseTimestamp(string line) =>
        DateTime.ParseExact(line[1..13], "HH:mm:ss.fff", CultureInfo.InvariantCulture);
}
