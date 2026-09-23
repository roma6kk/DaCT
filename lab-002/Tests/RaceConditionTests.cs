using OnlineStore.Shared;

namespace OnlineStore.Tests;

public sealed class RaceConditionTests
{
    [Fact]
    public async Task TwoClients_BuyLastItem_BothGetSuccess()
    {
        await using var server = new TestServer();
        var request = new Request("CreateOrder", new Dictionary<string, string>
        {
            ["customerId"] = "1", ["productId"] = "4", ["quantity"] = "1"
        });

        Assert.Equal(1, server.Logic.Products.Single(product => product.Id == 4).Stock);

        var responses = await Task.WhenAll(
            TestServer.SendAsync(server.Port, request),
            TestServer.SendAsync(server.Port, request));

        Assert.All(responses, response => Assert.True(response.Success));
        Assert.All(responses, response => Assert.Equal("OrderCreated", response.Event));
        Assert.Equal(-1, server.Logic.Products.Single(product => product.Id == 4).Stock);
    }
}
