using OnlineStore.Server;
using OnlineStore.Shared;

namespace OnlineStore.Tests;

public sealed class RaceConditionTests
{
    private static readonly Dictionary<string, string> LastItemOrder = new()
    {
        ["customerId"] = "1", ["productId"] = "4", ["quantity"] = "1"
    };

    [Fact]
    public async Task TwoClients_BuyLastItem_ExactlyOneSucceeds()
    {
        await using var server = new TestServer();
        var request = new Request("CreateOrder", LastItemOrder);

        Assert.Equal(1, ((BusinessLogic)server.Logic).Products.Single(product => product.Id == 4).Stock);

        var responses = await Task.WhenAll(
            TestServer.SendAsync(server.Port, request),
            TestServer.SendAsync(server.Port, request));

        Assert.Single(responses.Where(response => response.Success));
        Assert.Single(responses.Where(response => !response.Success));
        Assert.Equal(0, ((BusinessLogic)server.Logic).Products.Single(product => product.Id == 4).Stock);
        Assert.Contains(server.Logs, line => line.Contains("SUCCESS"));
        Assert.Contains(server.Logs, line => line.Contains("FAIL"));
    }
}