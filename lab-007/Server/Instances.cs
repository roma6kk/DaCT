using Npgsql;
using OnlineStore.Shared;

namespace OnlineStore.Server;


public sealed class DbInstance(string name, string connectionString)
{
    public string Name { get; } = name;

    public string ConnectionString { get; } = connectionString;

    
    public volatile bool SimulatedFailure;
}

public sealed class NoInstanceAvailableException(string message) : Exception(message);




public sealed class InstanceManager
{
    private readonly Action<string> log;

    public InstanceManager(string primaryConnectionString, string? replicaConnectionString, Action<string>? log = null)
    {
        this.log = log ?? (_ => { });
        Primary = new DbInstance("Primary", primaryConnectionString);
        Replica = replicaConnectionString is null ? null : new DbInstance("Replica", replicaConnectionString);
    }

    public DbInstance Primary { get; }

    public DbInstance? Replica { get; }

    
    public volatile bool ReplicaInSync = true;

    public static string Timestamp() => DateTime.Now.ToString("HH:mm:ss.fff");

    
    public async Task<bool> IsAvailableAsync(DbInstance instance, int clientId)
    {
        log($"[{Timestamp()}] Client {clientId} → {instance.Name} → CHECK");

        if (instance.SimulatedFailure)
        {
            log($"[{Timestamp()}] Client {clientId} → {instance.Name} → {InstanceStatus.Unavailable} | имитация отказа");
            return false;
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(instance.ConnectionString) { Timeout = 2 };
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync();
        }
        catch (Exception exception)
        {
            log($"[{Timestamp()}] Client {clientId} → {instance.Name} → {InstanceStatus.Unavailable} | {exception.Message}");
            return false;
        }

        log($"[{Timestamp()}] Client {clientId} → {instance.Name} → {InstanceStatus.Available}");
        return true;
    }

    
    
    
    
    public async Task<DbInstance> ResolveAsync(int clientId)
    {
        
        if (Replica is null)
        {
            if (Primary.SimulatedFailure)
                throw new NoInstanceAvailableException("Primary недоступен, резервная копия не настроена");
            return Primary;
        }

        if (await IsAvailableAsync(Primary, clientId))
            return Primary;

        log($"[{Timestamp()}] Client {clientId} → Failover → START");
        if (!await IsAvailableAsync(Replica, clientId))
        {
            log($"[{Timestamp()}] Client {clientId} → Failover → FAIL | оба экземпляра недоступны");
            throw new NoInstanceAvailableException("Оба экземпляра недоступны: Primary и Replica. Повторите запрос позже");
        }

        log($"[{Timestamp()}] Client {clientId} → Request redirected to Replica");
        return Replica;
    }

    
    public async Task<DbInstance> RequireAsync(DbInstance? instance, int clientId)
    {
        if (instance is null)
            throw new NoInstanceAvailableException("Резервная копия (Replica) не настроена");
        if (!await IsAvailableAsync(instance, clientId))
            throw new NoInstanceAvailableException($"{instance.Name} недоступен");
        return instance;
    }

    public async Task<InstancesInfo> GetStateAsync(int clientId)
    {
        var primary = await IsAvailableAsync(Primary, clientId);
        var replica = Replica is not null && await IsAvailableAsync(Replica, clientId);
        return new InstancesInfo(
            primary ? InstanceStatus.Available : InstanceStatus.Unavailable,
            Replica is null ? "NOT_CONFIGURED" : replica ? InstanceStatus.Available : InstanceStatus.Unavailable,
            ReplicaInSync);
    }
}
