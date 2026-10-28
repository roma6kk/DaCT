using OnlineStore.Shared;

namespace OnlineStore.Server;

public sealed class ClusterRequestProcessor(IRequestProcessor inner, ClusterCoordinator cluster, Action<string> log)
    : IRequestProcessor
{
    public Task<Response> HandleAsync(Request? request, int clientId)
    {
        if (request?.Operation is not (string operation and ("ClusterState" or "ClusterElect" or "ClusterCheckLeader"
                or "ClusterFail" or "ClusterRecover" or "ClusterPropose")))
            return inner.HandleAsync(request, clientId);

        try
        {
            return Task.FromResult(operation switch
            {
                "ClusterState" => new Response(true, "Состояние кластера", cluster.GetState(), "ClusterState"),
                "ClusterElect" => Elect(),
                "ClusterCheckLeader" => CheckLeader(),
                "ClusterFail" => ChangeNode(request.Data, cluster.Fail, "отказ"),
                "ClusterRecover" => ChangeNode(request.Data, cluster.Recover, "восстановление"),
                _ => Propose(request.Data)
            });
        }
        catch (Exception exception)
        {
            log($"[{Timestamp()}] Cluster ERROR | {exception.GetType().Name}: {exception.Message}");
            return Task.FromResult(new Response(false, $"Ошибка кластера: {exception.Message}"));
        }
    }

    private Response Elect()
    {
        var leader = cluster.Elect();
        return leader is { } id
            ? new(true, $"Лидер: Node {id}", cluster.GetState(), "ClusterState")
            : new(false, "Выбор невозможен: нет доступных узлов");
    }

    private Response CheckLeader()
    {
        var alive = cluster.CheckLeader();
        return new(true, alive ? "Лидер отвечает на heartbeat" : "Лидер не отвечает: HEARTBEAT TIMEOUT",
            cluster.GetState(), "ClusterState");
    }

    private Response ChangeNode(Dictionary<string, string>? data, Action<int> change, string action)
    {
        if (!int.TryParse(data?.GetValueOrDefault("node"), out var id))
            return new(false, "Некорректный ID узла");
        try
        {
            change(id);
            return new(true, $"Node {id}: {action} выполнено", cluster.GetState(), "ClusterState");
        }
        catch (ArgumentException exception)
        {
            return new(false, exception.Message);
        }
    }

    private Response Propose(Dictionary<string, string>? data)
    {
        var value = data?.GetValueOrDefault("value");
        if (string.IsNullOrWhiteSpace(value))
            return new(false, "Не задано предлагаемое значение");

        // Голоса приходят ключами vote_<ID> = YES / NO; некорректный голос — ошибка запроса.
        var decisions = new Dictionary<int, bool>();
        foreach (var (key, raw) in data!.Where(pair => pair.Key.StartsWith("vote_")))
        {
            if (!int.TryParse(key["vote_".Length..], out var nodeId) || raw is not (VoteResult.Yes or VoteResult.No))
                return new(false, $"Некорректный голос: {key} = {raw}");
            decisions[nodeId] = raw == VoteResult.Yes;
        }

        var result = cluster.Propose(value, decisions);
        return new(result.Consensus, result.Message, result, "ProposalResult");
    }

    private static string Timestamp() => DateTime.Now.ToString("HH:mm:ss.fff");
}
