using Npgsql;
using OnlineStore.Server;

namespace OnlineStore.Tests;






internal static class DbFixture
{
    private static readonly object Gate = new();
    private static bool initialized;

    public static string ConnectionString { get; } = BuildConnectionString();

    private static string BuildConnectionString()
    {
        if (Environment.GetEnvironmentVariable("ONLINESTORE_TEST_DB") is { Length: > 0 } explicitConnectionString)
            return explicitConnectionString;

        return new NpgsqlConnectionStringBuilder(Db.ConnectionString)
        {
            Database = "onlinestore_test"
        }.ConnectionString;
    }

    
    public static string ReplicaConnectionString { get; } = BuildReplicaConnectionString();

    private static string BuildReplicaConnectionString()
    {
        if (Environment.GetEnvironmentVariable("ONLINESTORE_TEST_REPLICA_DB") is { Length: > 0 } explicitConnectionString)
            return explicitConnectionString;

        return new NpgsqlConnectionStringBuilder(Db.ReplicaConnectionString)
        {
            Database = "onlinestore_test"
        }.ConnectionString;
    }

    public static void EnsureReplicaInitialized()
    {
        lock (Gate)
        {
            Db.EnsureDatabase(ReplicaConnectionString);
            Db.EnsureSchema(ReplicaConnectionString);
        }
    }

    public static void EnsureInitialized()
    {
        if (initialized)
            return;

        lock (Gate)
        {
            if (initialized)
                return;
            Db.EnsureDatabase(ConnectionString);
            Db.EnsureSchema(ConnectionString);
            initialized = true;
        }
    }

    
    public static void Reset()
    {
        EnsureInitialized();
        Db.ResetData(ConnectionString);
    }

    
    public static int Count(string sql)
    {
        EnsureInitialized();
        using var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public static int Stock(int productId) => Count($"SELECT stock FROM products WHERE id = {productId}");

    
    public static void Execute(string sql)
    {
        EnsureInitialized();
        using var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }
}
