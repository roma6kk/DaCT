using System.Diagnostics;
using System.Net.Sockets;
using OnlineStore.Shared;

namespace OnlineStore.Server;

public sealed class RequestHandler(BusinessLogic businessLogic, int clientId, Action<string> log)
{
    public async Task HandleAsync(TcpClient client)
    {
        using var connection = client;
        using var stream = connection.GetStream();
        using var reader = new StreamReader(stream);
        using var writer = new StreamWriter(stream) { AutoFlush = true };

        var request = await ReadRequestAsync(reader);
        var operation = request?.Operation ?? "Unknown";
        var taskId = Task.CurrentId;
        var threadId = Environment.CurrentManagedThreadId;
        log($"[{Timestamp()}] Client {clientId} START | Operation: {operation} | Task: {taskId} | Thread: {threadId}");

        var stopwatch = Stopwatch.StartNew();
        Response response;
        try
        {
            response = await businessLogic.HandleAsync(request);
        }
        catch (Exception exception)
        {
            response = new(false, "Ошибка обработки запроса");
            log($"[{Timestamp()}] Client {clientId} ERROR | {exception.GetType().Name}: {exception.Message}");
        }
        stopwatch.Stop();

        log($"[{Timestamp()}] Client {clientId} END | Operation: {operation} | Task: {taskId} | Thread: {threadId} | Elapsed: {stopwatch.Elapsed.TotalSeconds:F1} s");

        await SendResponseAsync(writer, response);
    }

    private static string Timestamp() => DateTime.Now.ToString("HH:mm:ss.fff");

    private static async Task<Request?> ReadRequestAsync(StreamReader reader)
    {
        try
        {
            return JsonProtocol.Deserialize<Request>(await reader.ReadLineAsync() ?? "");
        }
        catch
        {
            return null;
        }
    }

    private async Task SendResponseAsync(StreamWriter writer, Response response)
    {
        try
        {
            await writer.WriteLineAsync(JsonProtocol.Serialize(response));
        }
        catch (Exception exception)
        {
            log($"[{Timestamp()}] Client {clientId} ОТКЛЮЧЁН | {exception.GetType().Name}: {exception.Message}");
        }
    }
}
