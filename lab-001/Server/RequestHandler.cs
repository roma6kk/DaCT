using System.Net.Sockets;
using OnlineStore.Shared;

namespace OnlineStore.Server;

public sealed class RequestHandler(BusinessLogic businessLogic)
{
    public async Task HandleAsync(TcpClient client)
    {
        using var connection = client;
        using var stream = connection.GetStream();
        using var reader = new StreamReader(stream);
        using var writer = new StreamWriter(stream) { AutoFlush = true };

        var line = await reader.ReadLineAsync();
        Response response;
        try
        {
            response = businessLogic.Handle(JsonProtocol.Deserialize<Request>(line ?? ""));
        }
        catch
        {
            response = new(false, "Некорректный JSON");
        }

        await writer.WriteLineAsync(JsonProtocol.Serialize(response));
    }
}
