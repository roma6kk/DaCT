using OnlineStore.Shared;

namespace OnlineStore.Tests;


public sealed class LockConcurrencyTests
{
    private static Request OrderRequest(bool useLock) => new("CreateOrder", new Dictionary<string, string>
    {
        ["customerId"] = "1",
        ["productId"] = "4", 
        ["quantity"] = "1",
        ["useLock"] = useLock ? "1" : "0",
        ["useTransaction"] = "1",
        ["failAt"] = "0"
    });

    [Fact]
    public async Task TwoClients_LastItem_WithLock_ExactlyOneSucceeds()
    {
        DbFixture.Reset();
        await using var server = new TestServer();

        var responses = await Task.WhenAll(
            TestServer.SendAsync(server.Port, OrderRequest(useLock: true)),
            TestServer.SendAsync(server.Port, OrderRequest(useLock: true)));

        Assert.Single(responses, response => response.Success);
        Assert.Single(responses, response => !response.Success);
        Assert.Equal(0, DbFixture.Stock(4));                       
        Assert.Equal(1, DbFixture.Count("SELECT COUNT(*) FROM orders"));
        Assert.Contains(server.Logs, line => line.Contains("→ LOCK"));
        Assert.Contains(server.Logs, line => line.Contains("→ UNLOCK"));
        Assert.Contains(server.Logs, line => line.Contains("SUCCESS"));
        Assert.Contains(server.Logs, line => line.Contains("FAIL"));
    }

    [Fact]
    public async Task TwoClients_LastItem_WithoutLock_BothSucceed_LostUpdate()
    {
        DbFixture.Reset();
        
        await using var server = new TestServer(raceWindow: TimeSpan.FromSeconds(2));

        var responses = await Task.WhenAll(
            TestServer.SendAsync(server.Port, OrderRequest(useLock: false)),
            TestServer.SendAsync(server.Port, OrderRequest(useLock: false)));

        
        Assert.Equal(2, responses.Count(response => response.Success));
        Assert.Equal(2, DbFixture.Count("SELECT COUNT(*) FROM orders")); 
        Assert.Equal(0, DbFixture.Stock(4)); 
        Assert.DoesNotContain(server.Logs, line => line.Contains("→ LOCK"));
    }
}
