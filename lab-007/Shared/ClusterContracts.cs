namespace OnlineStore.Shared;

public static class NodeStatus
{
    public const string Available = "AVAILABLE";
    public const string Failed = "FAILED";
}

public static class NodeRole
{
    public const string Leader = "LEADER";
    public const string Follower = "FOLLOWER";
}

public static class VoteResult
{
    public const string Yes = "YES";
    public const string No = "NO";
    public const string NoResponse = "NO RESPONSE";
}

public sealed record NodeInfo(int Id, string Status, string Role, string? Value);

public sealed record ClusterState(List<NodeInfo> Nodes, int? LeaderId, int Majority, string? CommittedValue);

public sealed record ProposalResult(
    bool Consensus,
    string Value,
    int Yes,
    int No,
    int NoResponse,
    int Majority,
    Dictionary<int, string> Votes,
    string Message);
