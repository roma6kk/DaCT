using OnlineStore.Shared;

namespace OnlineStore.Server;

public sealed class ClusterCoordinator
{
    private readonly object gate = new();
    private readonly List<ClusterNode> nodes;
    private readonly Action<string> log;

    public ClusterCoordinator(Action<string>? log = null, int nodeCount = 5)
    {
        this.log = log ?? (_ => { });
        nodes = Enumerable.Range(1, nodeCount).Select(id => new ClusterNode(id)).ToList();
    }

    public IReadOnlyList<ClusterNode> Nodes => nodes;

    public int Majority => nodes.Count / 2 + 1;

    public int? LeaderId { get; private set; }

    public string? CommittedValue { get; private set; }

    public ClusterNode Node(int id) =>
        nodes.SingleOrDefault(node => node.Id == id) ?? throw new ArgumentException($"Нет узла с ID {id}");

    public int? Elect()
    {
        lock (gate)
        {
            log($"[{Timestamp()}] Election started");

            var available = new List<ClusterNode>();
            foreach (var node in nodes.OrderBy(node => node.Id))
            {
                if (Heartbeat(node))
                {
                    log($"[{Timestamp()}] {node.Name} AVAILABLE");
                    available.Add(node);
                }
                else
                {
                    log($"[{Timestamp()}] {node.Name} excluded | {node.Status}");
                }
            }

            foreach (var node in nodes)
                node.Role = NodeRole.Follower;

            if (available.Count == 0)
            {
                LeaderId = null;
                log($"[{Timestamp()}] Выбор невозможен: нет доступных узлов");
                return null;
            }

            var leader = available.MaxBy(node => node.Id)!;
            leader.Role = NodeRole.Leader;
            LeaderId = leader.Id;
            log($"[{Timestamp()}] {leader.Name} elected as LEADER");
            return leader.Id;
        }
    }

    public bool CheckLeader()
    {
        lock (gate)
        {
            if (LeaderId is not { } leaderId)
            {
                log($"[{Timestamp()}] Лидер не выбран");
                return false;
            }

            var leader = Node(leaderId);
            var alive = Heartbeat(leader);
            if (!alive)
                log($"[{Timestamp()}] {leader.Name} is LEADER → HEARTBEAT TIMEOUT");
            return alive;
        }
    }

    public void Fail(int id)
    {
        lock (gate)
        {
            Node(id).Fail();
            log($"[{Timestamp()}] Node {id} → FAILED | имитация отказа");
        }
    }

    public void Recover(int id)
    {
        lock (gate)
        {
            Node(id).Recover();
            log($"[{Timestamp()}] Node {id} → AVAILABLE | восстановление");
        }
    }

    // votes: голос каждого узла, заданный клиентом (true = YES, false = NO). Не указан — YES.
    public ProposalResult Propose(string value, IReadOnlyDictionary<int, bool>? decisions = null)
    {
        lock (gate)
        {
            if (LeaderId is not { } current || !Node(current).IsAvailable)
            {
                log($"[{Timestamp()}] Лидер недоступен → повторный выбор перед предложением");
                if (Elect() is null)
                    return NotAchieved(value, new Dictionary<int, string>(), "Нет доступного лидера");
            }

            var leader = Node(LeaderId!.Value);
            log($"[{Timestamp()}] {leader.Name} (LEADER) → PROPOSE {value}");

            var votes = new Dictionary<int, string>();
            foreach (var node in nodes.OrderBy(node => node.Id))
            {
                votes[node.Id] = Ask(node, value, decisions?.GetValueOrDefault(node.Id, true) ?? true);
                log($"[{Timestamp()}] {node.Name} → {votes[node.Id]}");
            }

            var yes = votes.Values.Count(vote => vote == VoteResult.Yes);
            var no = votes.Values.Count(vote => vote == VoteResult.No);
            var noResponse = votes.Values.Count(vote => vote == VoteResult.NoResponse);

            log($"[{Timestamp()}] YES = {yes}");
            log($"[{Timestamp()}] Majority = {Majority}");

            if (yes < Majority)
                return NotAchieved(value, votes, $"Голосов YES {yes} из нужных {Majority}: консенсус не достигнут, решение не принято");

            foreach (var node in nodes)
                node.Commit(value);
            CommittedValue = value;
            log($"[{Timestamp()}] CONSENSUS ACHIEVED | {value}");
            return new ProposalResult(true, value, yes, no, noResponse, Majority, votes,
                $"CONSENSUS ACHIEVED: {value}");
        }
    }

    public ClusterState GetState() => new(
        nodes.Select(node => node.ToInfo()).ToList(), LeaderId, Majority, CommittedValue);

    private ProposalResult NotAchieved(string value, Dictionary<int, string> votes, string message)
    {
        log($"[{Timestamp()}] CONSENSUS NOT ACHIEVED | {message}");
        var yes = votes.Values.Count(vote => vote == VoteResult.Yes);
        var no = votes.Values.Count(vote => vote == VoteResult.No);
        var noResponse = votes.Values.Count(vote => vote == VoteResult.NoResponse);
        return new ProposalResult(false, value, yes, no, noResponse, Majority, votes,
            $"CONSENSUS NOT ACHIEVED: {message}");
    }

    private string Ask(ClusterNode node, string value, bool approve)
    {
        try
        {
            var reply = node.Receive(new ClusterMessage(LeaderId ?? 0, node.Id, "PROPOSE", value, approve));
            return reply.Vote ?? VoteResult.NoResponse;
        }
        catch (NodeUnavailableException)
        {
            return VoteResult.NoResponse;
        }
    }

    private bool Heartbeat(ClusterNode node)
    {
        try
        {
            node.Receive(new ClusterMessage(0, node.Id, "HEARTBEAT"));
            return true;
        }
        catch (NodeUnavailableException)
        {
            node.Fail();
            return false;
        }
    }

    private static string Timestamp() => DateTime.Now.ToString("HH:mm:ss.fff");
}
