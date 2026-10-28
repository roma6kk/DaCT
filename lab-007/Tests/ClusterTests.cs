using OnlineStore.Server;
using OnlineStore.Shared;

namespace OnlineStore.Tests;

public sealed class ClusterTests
{
    private static ClusterCoordinator NewCluster() => new(_ => { });

    private static ClusterNode LeaderOf(ClusterCoordinator cluster) =>
        cluster.Nodes.Single(node => node.Role == NodeRole.Leader);

    private static Dictionary<int, bool> AllVotes(bool yes) => Enumerable.Range(1, 5).ToDictionary(id => id, _ => yes);

    [Fact]
    public void CreatesFiveNodesWithUniqueIds()
    {
        var cluster = NewCluster();

        Assert.Equal(5, cluster.Nodes.Count);
        Assert.Equal(5, cluster.Nodes.Select(node => node.Id).Distinct().Count());
        Assert.All(cluster.Nodes, node => Assert.Equal(NodeStatus.Available, node.Status));
    }

    [Fact]
    public void Elect_AllAvailable_ChoosesMaxId()
    {
        var cluster = NewCluster();

        Assert.Equal(5, cluster.Elect());
        Assert.Equal(NodeRole.Leader, cluster.Node(5).Role);
    }

    [Fact]
    public void Elect_NeverChoosesFailedNode()
    {
        var cluster = NewCluster();
        cluster.Fail(5);

        Assert.Equal(4, cluster.Elect());
        Assert.Equal(NodeStatus.Failed, cluster.Node(5).Status);
    }

    [Fact]
    public void Elect_NoAvailableNodes_ReturnsNull()
    {
        var cluster = NewCluster();
        foreach (var node in cluster.Nodes)
            cluster.Fail(node.Id);

        Assert.Null(cluster.Elect());
        Assert.Null(cluster.LeaderId);
    }

    [Fact]
    public void Elect_AlwaysLeavesExactlyOneLeader()
    {
        var cluster = NewCluster();
        cluster.Elect();
        cluster.Fail(5);
        cluster.Elect();

        Assert.Equal(1, cluster.Nodes.Count(node => node.Role == NodeRole.Leader));
    }

    [Fact]
    public void CheckLeader_FailedLeader_DetectedAsTimeout()
    {
        var cluster = NewCluster();
        cluster.Elect();
        cluster.Fail(5);

        Assert.False(cluster.CheckLeader());
    }

    [Fact]
    public void ReElect_AfterLeaderFailure_SelectsNextNode()
    {
        var cluster = NewCluster();
        cluster.Elect();
        cluster.Fail(5);
        cluster.CheckLeader();

        Assert.Equal(4, cluster.Elect());
        Assert.Equal(4, LeaderOf(cluster).Id);
    }

    [Fact]
    public void Majority_IsHalfPlusOneOfAllNodes()
    {
        Assert.Equal(3, NewCluster().Majority);
    }

    [Fact]
    public void Propose_AllAvailable_ConsensusAchieved()
    {
        var cluster = NewCluster();
        cluster.Elect();

        var result = cluster.Propose("ConfigurationVersion=10");

        Assert.True(result.Consensus);
        Assert.Equal(5, result.Yes);
        Assert.Equal("ConfigurationVersion=10", cluster.CommittedValue);
        Assert.All(cluster.Nodes, node => Assert.Equal("ConfigurationVersion=10", node.Value));
    }

    [Fact]
    public void Propose_OneNodeFailed_ConsensusStillAchieved()
    {
        var cluster = NewCluster();
        cluster.Elect();
        cluster.Fail(5);
        cluster.Elect();

        var result = cluster.Propose("v11");

        Assert.True(result.Consensus);
        Assert.Equal(4, result.Yes);
        Assert.Equal(VoteResult.NoResponse, result.Votes[5]);
    }

    [Fact]
    public void Propose_ThreeAvailable_ConsensusAchievedWithMajority()
    {
        var cluster = NewCluster();
        cluster.Fail(4);
        cluster.Fail(5);
        cluster.Elect();

        var result = cluster.Propose("v12");

        Assert.True(result.Consensus);
        Assert.Equal(3, result.Yes);
        Assert.Equal(3, result.Majority);
    }

    [Fact]
    public void Propose_TwoAvailable_ConsensusNotAchieved_AndNothingCommitted()
    {
        var cluster = NewCluster();
        cluster.Fail(3);
        cluster.Fail(4);
        cluster.Fail(5);
        cluster.Elect();

        var result = cluster.Propose("v13");

        Assert.False(result.Consensus);
        Assert.Equal(2, result.Yes);
        Assert.Equal(3, result.NoResponse);
        Assert.Null(cluster.CommittedValue);
        Assert.All(cluster.Nodes, node => Assert.Null(node.Value));
    }

    [Fact]
    public void Propose_ManualNoVotes_AllNo_ConsensusNotAchieved()
    {
        var cluster = NewCluster();
        cluster.Elect();
        cluster.Propose("v10");

        var result = cluster.Propose("v9", AllVotes(false));

        Assert.False(result.Consensus);
        Assert.Equal(5, result.No);
        Assert.Equal("v10", cluster.CommittedValue);
    }

    [Fact]
    public void Propose_OneNoVote_MajorityStillAchieved()
    {
        var cluster = NewCluster();
        cluster.Elect();

        var result = cluster.Propose("v1", new Dictionary<int, bool> { [4] = false });

        Assert.True(result.Consensus);
        Assert.Equal(4, result.Yes);
        Assert.Equal(1, result.No);
        Assert.Equal(VoteResult.No, result.Votes[4]);
    }

    [Fact]
    public void Propose_TwoYesThreeNo_NoMajority_NothingCommitted()
    {
        var cluster = NewCluster();
        cluster.Elect();

        var result = cluster.Propose("v1", new Dictionary<int, bool> { [1] = false, [2] = false, [3] = false });

        Assert.False(result.Consensus);
        Assert.Equal(2, result.Yes);
        Assert.Null(cluster.CommittedValue);
    }

    [Fact]
    public void Propose_FailedNodeCannotVote_EvenIfClientSetYes()
    {
        var cluster = NewCluster();
        cluster.Fail(5);
        cluster.Elect();

        var result = cluster.Propose("v1", AllVotes(true));

        Assert.Equal(VoteResult.NoResponse, result.Votes[5]);
        Assert.Equal(4, result.Yes);
    }

    [Fact]
    public void Propose_WithoutLeader_ElectsFirstAndProposes()
    {
        var cluster = NewCluster();

        var result = cluster.Propose("v1");

        Assert.True(result.Consensus);
        Assert.Equal(5, cluster.LeaderId);
    }

    [Fact]
    public void Propose_AfterLeaderChange_StillReachesConsensus()
    {
        var cluster = NewCluster();
        cluster.Elect();
        cluster.Propose("v10");
        cluster.Fail(5);
        cluster.CheckLeader();
        cluster.Elect();

        var result = cluster.Propose("v11");

        Assert.True(result.Consensus);
        Assert.Equal(4, cluster.LeaderId);
    }

    [Fact]
    public void FailedNode_CannotReceiveMessages()
    {
        var node = new ClusterNode(2);
        node.Fail();

        Assert.Throws<NodeUnavailableException>(() => node.Receive(new ClusterMessage(1, 2, "HEARTBEAT")));
    }

    [Fact]
    public void Node_InvalidMessageType_ReturnsErrorReply()
    {
        var node = new ClusterNode(1);

        var reply = node.Receive(new ClusterMessage(2, 1, "GARBAGE"));

        Assert.False(reply.Ok);
    }

    [Fact]
    public async Task ConcurrentProposals_DoNotCorruptState()
    {
        var cluster = NewCluster();
        cluster.Elect();

        var tasks = Enumerable.Range(1, 20)
            .Select(n => Task.Run(() => cluster.Propose($"v{n}")))
            .ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(1, cluster.Nodes.Count(node => node.Role == NodeRole.Leader));
        Assert.NotNull(cluster.CommittedValue);
    }
}
