using OnlineStore.Shared;

namespace OnlineStore.Tests;


public sealed class ResourceOperationTests
{
    private static T? Data<T>(Response response) =>
        JsonProtocol.Deserialize<T>(JsonProtocol.Serialize(response.Data));

    [Fact]
    public async Task GetProducts_ReadsFromDatabase()
    {
        DbFixture.Reset();
        await using var server = new TestServer();

        var response = await TestServer.SendAsync(server.Port, new Request("GetProducts"));

        Assert.True(response.Success);
        var products = Data<List<Product>>(response);
        Assert.NotNull(products);
        Assert.Equal(4, products!.Count);
        Assert.Equal(1, products.Single(product => product.Id == 4).Stock);
        Assert.Equal(5, products.Single(product => product.Id == 1).Stock);
    }

    [Fact]
    public async Task CreateOrder_WritesOrderAndStockToDatabase()
    {
        DbFixture.Reset();
        await using var server = new TestServer();

        var response = await TestServer.SendAsync(server.Port, new Request("CreateOrder",
            new Dictionary<string, string> { ["customerId"] = "1", ["productId"] = "1", ["quantity"] = "2" }));

        Assert.True(response.Success);
        Assert.Equal("OrderCreated", response.Event);
        Assert.Equal(3, DbFixture.Stock(1));                                  
        Assert.Equal(1, DbFixture.Count("SELECT COUNT(*) FROM orders"));      
        Assert.Equal(1, DbFixture.Count("SELECT COUNT(*) FROM order_items")); 
        Assert.Equal(1, DbFixture.Count(
            "SELECT COUNT(*) FROM order_items i JOIN orders o ON o.id = i.order_id WHERE o.customer_id = 1"));
    }

    [Fact]
    public async Task PayOrder_CreatesPaymentInDatabase()
    {
        DbFixture.Reset();
        await using var server = new TestServer();
        await TestServer.SendAsync(server.Port, new Request("CreateOrder",
            new Dictionary<string, string> { ["customerId"] = "1", ["productId"] = "1", ["quantity"] = "1" }));

        var payResponse = await TestServer.SendAsync(server.Port, new Request("PayOrder",
            new Dictionary<string, string> { ["orderId"] = "1" }));

        Assert.True(payResponse.Success);
        Assert.Equal(1, DbFixture.Count("SELECT COUNT(*) FROM payments"));
        Assert.Equal("Paid", (await TestServer.SendAsync(server.Port, new Request("GetState")))
            .Data is { } data ? ReadState(data).Orders.Single().Status : "");
    }

    [Fact]
    public async Task GetState_ShowsOrders_And_Reset_RestoresData()
    {
        DbFixture.Reset();
        await using var server = new TestServer();
        await TestServer.SendAsync(server.Port, new Request("CreateOrder",
            new Dictionary<string, string> { ["customerId"] = "1", ["productId"] = "4", ["quantity"] = "1" }));

        var state = ReadState((await TestServer.SendAsync(server.Port, new Request("GetState"))).Data!);
        Assert.Single(state.Orders);
        Assert.Equal(0, state.Products.Single(product => product.Id == 4).Stock);

        var reset = await TestServer.SendAsync(server.Port, new Request("Reset"));

        Assert.True(reset.Success);
        Assert.Empty(ReadState(reset.Data!).Orders);
        Assert.Equal(1, DbFixture.Stock(4));
    }

    private static DatabaseState ReadState(object data) =>
        JsonProtocol.Deserialize<DatabaseState>(JsonProtocol.Serialize(data))!;
}
