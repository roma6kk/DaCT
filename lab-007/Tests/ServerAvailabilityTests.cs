using OnlineStore.Shared;

namespace OnlineStore.Tests;


public sealed class ServerAvailabilityTests
{
    [Fact]
    public async Task Server_IsReachable_AcceptsRequest()
    {
        DbFixture.Reset();
        await using var server = new TestServer();

        var response = await TestServer.SendAsync(server.Port, new Request("GetProducts"));

        Assert.True(response.Success);
        Assert.NotNull(response.Data);
    }

    [Fact]
    public async Task Server_ReturnsDatabaseState_OnGetState()
    {
        DbFixture.Reset();
        await using var server = new TestServer();

        var response = await TestServer.SendAsync(server.Port, new Request("GetState"));

        Assert.True(response.Success);
        Assert.Equal("DatabaseState", response.Event);
        var state = JsonProtocol.Deserialize<DatabaseState>(JsonProtocol.Serialize(response.Data));
        Assert.NotNull(state);
        Assert.Equal(4, state!.Products.Count);
        Assert.Empty(state.Orders);
    }
}
