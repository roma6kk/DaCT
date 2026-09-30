using OnlineStore.Server.SemaphoreSlimSync;
using OnlineStore.Shared;

namespace OnlineStore.Tests;

public sealed class SemaphoreSlimTests
{
    [Fact]
    public async Task TwoClients_BuyLastItem_ExactlyOneSucceeds()
    {
        var logCapture = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var logic = new SemaphoreSlimBusinessLogic(log: logCapture.Enqueue);
        await using var server = new TestServer(logic: logic);
        var request = new Request("CreateOrder", new Dictionary<string, string>
        {
            ["customerId"] = "1", ["productId"] = "4", ["quantity"] = "1"
        });

        var responses = await Task.WhenAll(
            TestServer.SendAsync(server.Port, request),
            TestServer.SendAsync(server.Port, request));

        Assert.Single(responses.Where(response => response.Success));
        Assert.Single(responses.Where(response => !response.Success));
        Assert.Equal(0, logic.Products.Single(product => product.Id == 4).Stock);
        Assert.Contains(logCapture, line => line.Contains("LOCK"));
        Assert.Contains(logCapture, line => line.Contains("UNLOCK"));
    }
}