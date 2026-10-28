using OnlineStore.Shared;

namespace OnlineStore.Client;

public static class ClusterScenarios
{
    public static async Task RunAsync(Client client)
    {
        Console.WriteLine("=== Эксперименты ЛР №7: выбор лидера и консенсус ===");

        Console.WriteLine("\n[Часть 2] Выбор лидера (все узлы доступны)");
        await Send(client, RequestBuilder.ClusterElect());
        await ShowAsync(client);

        Console.WriteLine("\n[Эксперимент 1] Все узлы доступны (5 / 5)");
        await Send(client, RequestBuilder.ClusterPropose("ConfigurationVersion=10"));
        await ShowAsync(client);

        Console.WriteLine("\n[Часть 3] Отказ текущего лидера Node 5");
        await Send(client, RequestBuilder.ClusterFail(5));
        await Send(client, RequestBuilder.ClusterCheckLeader());

        Console.WriteLine("\n[Часть 4] Повторный выбор лидера");
        await Send(client, RequestBuilder.ClusterElect());
        await ShowAsync(client);

        Console.WriteLine("\n[Эксперимент 2] Отказ одного узла (4 / 5), Node 1 голосует NO");
        await Send(client, RequestBuilder.ClusterPropose("ConfigurationVersion=11", new Dictionary<int, bool> { [1] = false }));
        await ShowAsync(client);

        Console.WriteLine("\n[Эксперимент 3] Отказ двух узлов (3 / 5)");
        await Send(client, RequestBuilder.ClusterFail(4));
        await Send(client, RequestBuilder.ClusterElect());
        await Send(client, RequestBuilder.ClusterPropose("ConfigurationVersion=12"));
        await ShowAsync(client);

        Console.WriteLine("\n[Эксперимент 4] Отказ трех узлов (2 / 5)");
        await Send(client, RequestBuilder.ClusterRecover(4));
        await Send(client, RequestBuilder.ClusterRecover(5));
        await Send(client, RequestBuilder.ClusterFail(3));
        await Send(client, RequestBuilder.ClusterFail(4));
        await Send(client, RequestBuilder.ClusterFail(5));
        await Send(client, RequestBuilder.ClusterElect());
        await Send(client, RequestBuilder.ClusterPropose("ConfigurationVersion=13"));
        await ShowAsync(client);
    }

    private static async Task Send(Client client, Request request)
    {
        var response = await client.SendAsync(request);
        Console.WriteLine($"{(response.Success ? "OK" : "FAIL")}: {response.Message}");
    }

    public static async Task ShowAsync(Client client)
    {
        var response = await client.SendAsync(RequestBuilder.ClusterState());
        if (!response.Success || response.Data is null)
        {
            Console.WriteLine($"FAIL: {response.Message}");
            return;
        }

        var state = JsonProtocol.Deserialize<ClusterState>(JsonProtocol.Serialize(response.Data));
        if (state is null)
            return;

        Console.WriteLine($"Leader: {(state.LeaderId is { } id ? $"Node {id}" : "нет")} | Majority = {state.Majority} | " +
                          $"Committed: {state.CommittedValue ?? "нет"}");
        foreach (var node in state.Nodes)
            Console.WriteLine($"  Node {node.Id}: {node.Status,-9} {node.Role,-8} Value={node.Value ?? "-"}");
    }
}
