using OnlineStore.Server;
using OnlineStore.Shared;

namespace OnlineStore.Tests;


public sealed class ReplicationTests
{
    private static T? Data<T>(Response response) =>
        JsonProtocol.Deserialize<T>(JsonProtocol.Serialize(response.Data));

    private static Task<Response> SendAsync(IRequestProcessor logic, string operation,
        Dictionary<string, string>? data = null) =>
        logic.HandleAsync(new Request(operation, data), clientId: 1);

    
    private static (InstanceManager Instances, BusinessLogic Logic) CreateSystem()
    {
        DbFixture.EnsureInitialized();
        DbFixture.EnsureReplicaInitialized();

        var instances = new InstanceManager(DbFixture.ConnectionString, DbFixture.ReplicaConnectionString);
        var logic = new BusinessLogic(instances, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
        return (instances, logic);
    }

    private static async Task<ConsistencyReport> CompareAsync(IRequestProcessor logic) =>
        Data<ConsistencyReport>(await SendAsync(logic, "Compare"))!;

    private static Dictionary<string, string> Order(int productId, int quantity = 1) => new()
    {
        ["customerId"] = "1",
        ["productId"] = productId.ToString(),
        ["quantity"] = quantity.ToString()
    };

    private static int StockIn(string connectionString, int productId) =>
        Convert.ToInt32(new Npgsql.NpgsqlCommand(
            $"SELECT stock FROM products WHERE id = {productId}", OpenConnection(connectionString)).ExecuteScalar());

    private static Npgsql.NpgsqlConnection OpenConnection(string connectionString)
    {
        var connection = new Npgsql.NpgsqlConnection(connectionString);
        connection.Open();
        return connection;
    }

    

    [Fact]
    public async Task InitialState_PrimaryEqualsReplica()
    {
        var (_, logic) = CreateSystem();
        Assert.True((await SendAsync(logic, "Reset")).Success);

        var report = await CompareAsync(logic);

        Assert.True(report.Consistent, string.Join("; ", report.Differences));
    }

    [Fact]
    public async Task InstancesState_BothAvailableAtStart()
    {
        var (_, logic) = CreateSystem();

        var info = Data<InstancesInfo>(await SendAsync(logic, "GetInstancesState"));

        Assert.Equal(InstanceStatus.Available, info!.Primary);
        Assert.Equal(InstanceStatus.Available, info.Replica);
    }

    

    [Fact]
    public async Task Replication_AfterCreateOrder_CopiesAreEqual()
    {
        var (_, logic) = CreateSystem();
        await SendAsync(logic, "Reset");

        var response = await SendAsync(logic, "CreateOrder", Order(productId: 1));

        Assert.True(response.Success);
        Assert.Equal(4, StockIn(DbFixture.ConnectionString, 1));
        Assert.Equal(4, StockIn(DbFixture.ReplicaConnectionString, 1));
        Assert.True((await CompareAsync(logic)).Consistent);
    }

    [Fact]
    public async Task Replication_SeveralChanges_CopiesStayEqual()
    {
        var (_, logic) = CreateSystem();
        await SendAsync(logic, "Reset");

        await SendAsync(logic, "CreateOrder", Order(productId: 1));
        await SendAsync(logic, "CreateOrder", Order(productId: 2, quantity: 2));
        await SendAsync(logic, "CancelOrder", new Dictionary<string, string> { ["orderId"] = "1" });

        Assert.Equal(StockIn(DbFixture.ConnectionString, 1), StockIn(DbFixture.ReplicaConnectionString, 1));
        Assert.Equal(StockIn(DbFixture.ConnectionString, 2), StockIn(DbFixture.ReplicaConnectionString, 2));
        Assert.True((await CompareAsync(logic)).Consistent);
    }

    

    [Fact]
    public async Task PrimaryFailure_IsDetected_WithoutCrash()
    {
        var (instances, logic) = CreateSystem();
        await SendAsync(logic, "Reset");
        await SendAsync(logic, "SimulateFailure", new Dictionary<string, string> { ["instance"] = "Primary", ["enabled"] = "1" });

        var available = await instances.IsAvailableAsync(instances.Primary, clientId: 1);

        Assert.False(available);
    }

    [Fact]
    public async Task Failover_SelectsReplica_WhenPrimaryUnavailable()
    {
        var (instances, logic) = CreateSystem();
        await SendAsync(logic, "Reset");
        instances.Primary.SimulatedFailure = true;

        var selected = await instances.ResolveAsync(clientId: 1);

        Assert.Same(instances.Replica, selected);
    }

    [Fact]
    public async Task Failover_RequestsSucceedThroughReplica()
    {
        var (instances, logic) = CreateSystem();
        await SendAsync(logic, "Reset");
        instances.Primary.SimulatedFailure = true;

        var read = await SendAsync(logic, "GetProducts");
        var update = await SendAsync(logic, "CreateOrder", Order(productId: 3));

        Assert.True(read.Success);
        Assert.Equal("Replica", read.Instance);
        Assert.True(update.Success);
        Assert.Equal("Replica", update.Instance);
        Assert.Equal(11, StockIn(DbFixture.ReplicaConnectionString, 3));
        Assert.Equal(12, StockIn(DbFixture.ConnectionString, 3));
    }

    [Fact]
    public async Task BothUnavailable_ReturnsClearError()
    {
        var (instances, logic) = CreateSystem();
        instances.Primary.SimulatedFailure = true;
        instances.Replica!.SimulatedFailure = true;

        var response = await SendAsync(logic, "GetProducts");

        Assert.False(response.Success);
        Assert.Contains("Оба экземпляра недоступны", response.Message);
    }

    [Fact]
    public async Task ReplicaFailure_DuringReplication_IsReported_NotFatal()
    {
        var (instances, logic) = CreateSystem();
        await SendAsync(logic, "Reset");
        instances.Replica!.SimulatedFailure = true;

        var response = await SendAsync(logic, "CreateOrder", Order(productId: 1));

        Assert.True(response.Success);
        Assert.False(instances.ReplicaInSync);
        Assert.Equal(4, StockIn(DbFixture.ConnectionString, 1));
    }

    [Fact]
    public async Task UnknownInstance_ReturnsError()
    {
        var (_, logic) = CreateSystem();

        var response = await SendAsync(logic, "SimulateFailure",
            new Dictionary<string, string> { ["instance"] = "Unknown", ["enabled"] = "1" });

        Assert.False(response.Success);
    }

    

    [Fact]
    public async Task Recovery_SynchronizesPrimaryFromReplica()
    {
        var (instances, logic) = CreateSystem();
        await SendAsync(logic, "Reset");
        instances.Primary.SimulatedFailure = true;
        await SendAsync(logic, "CreateOrder", Order(productId: 3));

        var response = await SendAsync(logic, "RecoverPrimary");

        Assert.True(response.Success, response.Message);
        var report = Data<RecoveryReport>(response);
        Assert.NotEmpty(report!.DifferencesBefore);
        Assert.Empty(report.DifferencesAfter);
        Assert.True(report.Consistent);
        Assert.False(instances.Primary.SimulatedFailure);
    }

    [Fact]
    public async Task AfterRecovery_ControlOperation_KeepsCopiesEqual()
    {
        var (instances, logic) = CreateSystem();
        await SendAsync(logic, "Reset");
        instances.Primary.SimulatedFailure = true;
        await SendAsync(logic, "CreateOrder", Order(productId: 3));
        await SendAsync(logic, "RecoverPrimary");

        var control = await SendAsync(logic, "CreateOrder", Order(productId: 4));

        Assert.True(control.Success);
        Assert.Equal("Primary", control.Instance);
        Assert.True((await CompareAsync(logic)).Consistent);
    }

    [Fact]
    public async Task Recovery_WhilePrimaryStillDown_ReturnsError()
    {
        
        DbFixture.EnsureReplicaInitialized();
        var instances = new InstanceManager(
            "Host=127.0.0.1;Port=1;Database=missing;Username=x;Password=x", DbFixture.ReplicaConnectionString);
        var logic = new BusinessLogic(instances, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);

        var response = await SendAsync(logic, "RecoverPrimary");

        Assert.False(response.Success);
        Assert.Contains("Primary всё ещё недоступен", response.Message);
    }

    

    [Fact]
    public async Task Server_ContinuesServing_WhenPrimaryFails()
    {
        var (instances, logic) = CreateSystem();
        await SendAsync(logic, "Reset");
        await using var server = new TestServer(logic: logic);
        instances.Primary.SimulatedFailure = true;

        var response = await TestServer.SendAsync(server.Port, new Request("GetProducts"));

        Assert.True(response.Success);
        Assert.Equal("Replica", response.Instance);
    }
}
