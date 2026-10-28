using OnlineStore.Shared;

namespace OnlineStore.Server;

public sealed class NodeUnavailableException(int nodeId) : Exception($"Node {nodeId} не отвечает");

public sealed record ClusterMessage(int From, int To, string Type, string? Value = null, bool Approve = true);

public sealed record ClusterReply(bool Ok, string Text, string? Vote = null);

public sealed class ClusterNode(int id)
{
    private readonly object gate = new();

    public int Id { get; } = id;

    public string Name => $"Node {Id}";

    public string Status { get; private set; } = NodeStatus.Available;

    public string Role { get; set; } = NodeRole.Follower;

    public string? Value { get; private set; }

    public bool IsAvailable
    {
        get
        {
            lock (gate)
                return Status == NodeStatus.Available;
        }
    }

    public void Fail()
    {
        lock (gate)
        {
            Status = NodeStatus.Failed;
            Role = NodeRole.Follower;
        }
    }

    public void Recover()
    {
        lock (gate)
            Status = NodeStatus.Available;
    }

    public ClusterReply Receive(ClusterMessage message)
    {
        lock (gate)
        {
            if (Status != NodeStatus.Available)
                throw new NodeUnavailableException(Id);

            return message.Type switch
            {
                "HEARTBEAT" => new(true, "ALIVE"),
                "PROPOSE" => Vote(message.Approve),
                _ => new(false, $"Некорректное сообщение: неизвестный тип «{message.Type}»")
            };
        }
    }

    private static ClusterReply Vote(bool approve) =>
        approve ? new(true, "YES", VoteResult.Yes) : new(true, "NO", VoteResult.No);

    public void Commit(string value)
    {
        lock (gate)
        {
            if (Status != NodeStatus.Available)
                return;
            Value = value;
        }
    }

    public NodeInfo ToInfo() => new(Id, Status, Role, Value);
}
