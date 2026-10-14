using OnlineStore.Shared;

namespace OnlineStore.Tests;





public sealed class TransactionTests
{
    private static Request OrderRequest(bool useTransaction, int failAt) => new("CreateOrder",
        new Dictionary<string, string>
        {
            ["customerId"] = "1",
            ["productId"] = "1", 
            ["quantity"] = "1",
            ["useTransaction"] = useTransaction ? "1" : "0",
            ["useLock"] = "1",
            ["failAt"] = failAt.ToString()
        });

    [Fact]
    public async Task Experiment1_WithoutTransaction_NoError_BothOperationsSaved()
    {
        DbFixture.Reset();
        await using var server = new TestServer();

        var response = await TestServer.SendAsync(server.Port, OrderRequest(useTransaction: false, failAt: 0));

        Assert.True(response.Success);
        Assert.Equal(4, DbFixture.Stock(1));
        Assert.Equal(1, DbFixture.Count("SELECT COUNT(*) FROM orders"));
        Assert.Equal(1, DbFixture.Count("SELECT COUNT(*) FROM order_items"));
        Assert.DoesNotContain(server.Logs, line => line.Contains("COMMIT"));
    }

    [Fact]
    public async Task Experiment2_WithoutTransaction_Error_PartialStateRemains()
    {
        DbFixture.Reset();
        await using var server = new TestServer();

        var response = await TestServer.SendAsync(server.Port, OrderRequest(useTransaction: false, failAt: 1));

        Assert.False(response.Success);
        Assert.Contains("частичные изменения сохранены", response.Message);
        
        Assert.Equal(1, DbFixture.Count("SELECT COUNT(*) FROM orders"));
        Assert.Equal(5, DbFixture.Stock(1));
        Assert.Contains(server.Logs, line => line.Contains("ЧАСТИЧНО ИЗМЕНЕНО"));
    }

    [Fact]
    public async Task Experiment3_WithTransaction_NoError_CommitSavesBothOperations()
    {
        DbFixture.Reset();
        await using var server = new TestServer();

        var response = await TestServer.SendAsync(server.Port, OrderRequest(useTransaction: true, failAt: 0));

        Assert.True(response.Success);
        Assert.Contains("COMMIT", response.Message);
        Assert.Equal(4, DbFixture.Stock(1));
        Assert.Equal(1, DbFixture.Count("SELECT COUNT(*) FROM orders"));
        Assert.Contains(server.Logs, line => line.Contains("→ COMMIT"));
    }

    [Fact]
    public async Task Experiment4_WithTransaction_Error_RollbackLeavesNoChanges()
    {
        DbFixture.Reset();
        await using var server = new TestServer();

        var response = await TestServer.SendAsync(server.Port, OrderRequest(useTransaction: true, failAt: 1));

        Assert.False(response.Success);
        Assert.Contains("ROLLBACK", response.Message);
        Assert.Equal(0, DbFixture.Count("SELECT COUNT(*) FROM orders"));     
        Assert.Equal(0, DbFixture.Count("SELECT COUNT(*) FROM order_items"));
        Assert.Equal(5, DbFixture.Stock(1));                                 
        Assert.Contains(server.Logs, line => line.Contains("→ ROLLBACK"));
    }

    [Fact]
    public async Task Experiment5_WithTransaction_ErrorBeforeCommit_RollsBackExecutedOperations()
    {
        DbFixture.Reset();
        await using var server = new TestServer();

        
        var response = await TestServer.SendAsync(server.Port, OrderRequest(useTransaction: true, failAt: 2));

        Assert.False(response.Success);
        Assert.Equal(0, DbFixture.Count("SELECT COUNT(*) FROM orders"));
        Assert.Equal(5, DbFixture.Stock(1));
        Assert.Contains(server.Logs, line => line.Contains("→ ROLLBACK"));
    }

    [Fact]
    public async Task Experiment6_WithoutTransaction_ErrorAfterSecondOperation_ChangesRemain()
    {
        DbFixture.Reset();
        await using var server = new TestServer();

        
        var response = await TestServer.SendAsync(server.Port, OrderRequest(useTransaction: false, failAt: 2));

        Assert.False(response.Success);
        Assert.Equal(1, DbFixture.Count("SELECT COUNT(*) FROM orders"));
        Assert.Equal(4, DbFixture.Stock(1));
    }

    [Fact]
    public async Task CancelOrder_ReturnsStockToDatabase_InTransaction()
    {
        DbFixture.Reset();
        await using var server = new TestServer();
        await TestServer.SendAsync(server.Port, OrderRequest(useTransaction: true, failAt: 0));

        var response = await TestServer.SendAsync(server.Port, new Request("CancelOrder",
            new Dictionary<string, string> { ["orderId"] = "1" }));

        Assert.True(response.Success);
        Assert.Equal(5, DbFixture.Stock(1)); 
        Assert.Equal("Cancelled", (await TestServer.SendAsync(server.Port, new Request("GetState")))
            .Data is { } data
            ? JsonProtocol.Deserialize<DatabaseState>(JsonProtocol.Serialize(data))!.Orders.Single().Status
            : "");
    }
}
