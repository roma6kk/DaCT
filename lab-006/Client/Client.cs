using System.Globalization;
using System.Net.Sockets;
using OnlineStore.Shared;

namespace OnlineStore.Client;

public sealed class Client(string host = "127.0.0.1", int port = 5000, TimeSpan? clockOffset = null)
{
    private readonly TimeSpan clockOffset = clockOffset ?? TimeSpan.Zero;

    public TimeSpan ClockOffset => clockOffset;

    public TimeSpan? Offset { get; private set; }

    public TimeSpan? Rtt { get; private set; }

    public TimeSpan RttHalf => Rtt.HasValue ? TimeSpan.FromTicks(Rtt.Value.Ticks / 2) : TimeSpan.Zero;

    public DateTime LocalTime => DateTime.UtcNow + clockOffset;

    public DateTime CorrectedTime => LocalTime + (Offset ?? TimeSpan.Zero);

    public async Task<Response> SendAsync(Request request)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream);
            using var writer = new StreamWriter(stream) { AutoFlush = true };
            await writer.WriteLineAsync(JsonProtocol.Serialize(request));
            return JsonProtocol.Deserialize<Response>(await reader.ReadLineAsync() ?? "")
                ?? new(false, "Пустой ответ сервера");
        }
        catch (SocketException)
        {
            return new(false, "Сервер недоступен");
        }
        catch (IOException)
        {
            return new(false, "Ошибка сетевого взаимодействия");
        }
    }

    public async Task<TimeSyncResult> SyncTimeAsync()
    {
        var requestTime = LocalTime;
        var response = await SendAsync(new Request("TIME_REQUEST"));
        var responseTime = LocalTime;

        if (!response.Success || JsonProtocol.Deserialize<TimeInfo?>(JsonProtocol.Serialize(response.Data)) is not { } timeInfo)
            return new TimeSyncResult(false, LocalTime, null, null, null, null, response.Message);

        var serverTime = timeInfo.ServerTimeUtc;
        var rtt = responseTime - requestTime;
        var delay = TimeSpan.FromTicks(rtt.Ticks / 2);
        var offset = serverTime + delay - responseTime;

        Offset = offset;
        Rtt = rtt;

        return new TimeSyncResult(true, LocalTime, serverTime, offset, rtt, delay, null);
    }

    public static string FormatTime(DateTime value) => value.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
}

public sealed record TimeSyncResult(
    bool Success,
    DateTime ClientLocalTime,
    DateTime? ServerTime,
    TimeSpan? Offset,
    TimeSpan? Rtt,
    TimeSpan? EstimatedDelay,
    string? Error)
{
    public static readonly TimeSyncResult Failed = new(false, default, null, null, null, null, "Ошибка синхронизации времени");
}