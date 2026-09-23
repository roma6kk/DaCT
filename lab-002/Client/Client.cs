using System.Net.Sockets;
using OnlineStore.Shared;

namespace OnlineStore.Client;

public sealed class Client(string host = "127.0.0.1", int port = 5000)
{
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
}
