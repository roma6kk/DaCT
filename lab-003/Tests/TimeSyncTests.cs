using OnlineStore.Client;
using OnlineStore.Shared;

namespace OnlineStore.Tests;

public sealed class TimeSyncTests
{
    [Fact]
    public async Task TimeSync_ReturnsServerTimeAndRtt()
    {
        await using var server = new TestServer(timeResponseDelay: TimeSpan.FromMilliseconds(200));
        var client = new OnlineStore.Client.Client(port: server.Port);

        var result = await client.SyncTimeAsync();

        Assert.True(result.Success);
        Assert.NotNull(result.ServerTime);
        Assert.NotNull(result.Rtt);
        // 
        Assert.True(result.Rtt!.Value.TotalMilliseconds >= 200);
        // 
        Assert.Equal(result.Rtt!.Value.TotalMilliseconds / 2, result.EstimatedDelay!.Value.TotalMilliseconds, precision: 1);
        // 
        var expected = result.ServerTime!.Value + result.EstimatedDelay!.Value - result.ClientLocalTime;
        Assert.True(Math.Abs((expected - result.Offset!.Value).TotalMilliseconds) < 10);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(-5)]
    public async Task ClientClockOffset_IsDetectedBySync(double offsetSeconds)
    {
        await using var server = new TestServer(timeResponseDelay: TimeSpan.FromMilliseconds(200));
        var client = new OnlineStore.Client.Client(port: server.Port, clockOffset: TimeSpan.FromSeconds(offsetSeconds));

        var result = await client.SyncTimeAsync();

        Assert.True(result.Success);
        var compensation = -result.Offset!.Value.TotalSeconds;
        Assert.InRange(compensation - offsetSeconds, -0.5, 0.5);
    }

    [Fact]
    public async Task CorrectedTime_IsCloseToServerTime()
    {
        await using var server = new TestServer(timeResponseDelay: TimeSpan.Zero);
        var client = new OnlineStore.Client.Client(port: server.Port, clockOffset: TimeSpan.FromSeconds(7));

        await client.SyncTimeAsync();

        var corrected = client.CorrectedTime;
        var serverNow = DateTime.UtcNow;
        Assert.True(Math.Abs((corrected - serverNow).TotalSeconds) < 1);
    }

    [Fact]
    public async Task TimeRequest_LoggedAsTimeResponse()
    {
        await using var server = new TestServer(timeResponseDelay: TimeSpan.FromMilliseconds(100));
        var client = new OnlineStore.Client.Client(port: server.Port);

        var response = await client.SendAsync(RequestBuilder.TimeRequest());

        Assert.True(response.Success);
        Assert.Equal("TimeResponse", response.Event);
        Assert.Contains(server.Logs, line => line.Contains("TIME_RESPONSE"));
    }
}