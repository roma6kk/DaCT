using Npgsql;
using OnlineStore.Shared;

namespace OnlineStore.Server;

public sealed record ProductRow(int Id, string Name, decimal Price, int Stock);

public sealed record OrderRow(int Id, int CustomerId, string Status, decimal Total);

public sealed record ItemRow(int Id, int OrderId, int ProductId, int Quantity, decimal Price);

public sealed record PaymentRow(int Id, int OrderId, decimal Amount, string Status);


public sealed record Snapshot(List<ProductRow> Products, List<OrderRow> Orders, List<ItemRow> Items, List<PaymentRow> Payments)
{
    public int RowCount => Products.Count + Orders.Count + Items.Count + Payments.Count;
}






public sealed class Replicator(InstanceManager instances, Action<string> log)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task ReplicateAsync(int clientId)
    {
        if (instances.Replica is null)
            return;

        await gate.WaitAsync();
        try
        {
            log($"[{InstanceManager.Timestamp()}] Client {clientId} → Replication → START | Primary → Replica");

            var snapshot = await ReadAsync(instances.Primary.ConnectionString);
            log($"[{InstanceManager.Timestamp()}] Client {clientId} → Primary → READ | строк для передачи: {snapshot.RowCount}");

            if (!await instances.IsAvailableAsync(instances.Replica, clientId))
            {
                instances.ReplicaInSync = false;
                log($"[{InstanceManager.Timestamp()}] Client {clientId} → Replication → FAIL | Replica недоступна, копии расходятся");
                return;
            }

            await WriteAsync(instances.Replica.ConnectionString, snapshot);
            instances.ReplicaInSync = true;
            log($"[{InstanceManager.Timestamp()}] Client {clientId} → Replica → UPDATE | применено строк: {snapshot.RowCount}");
            log($"[{InstanceManager.Timestamp()}] Client {clientId} → Replication → SUCCESS");
        }
        catch (NpgsqlException exception)
        {
            instances.ReplicaInSync = false;
            log($"[{InstanceManager.Timestamp()}] Client {clientId} → Replication → FAIL | {exception.Message}");
        }
        finally
        {
            gate.Release();
        }
    }

    
    
    
    
    public async Task<(bool Ok, string Message, RecoveryReport? Report)> RecoverPrimaryAsync(int clientId)
    {
        var primary = instances.Primary;
        var replica = instances.Replica;
        if (replica is null)
            return (false, "Резервная копия не настроена, синхронизация невозможна", null);

        primary.SimulatedFailure = false;

        await gate.WaitAsync();
        try
        {
            if (!await instances.IsAvailableAsync(primary, clientId))
                return (false, "Primary всё ещё недоступен, восстановление не выполнено", null);

            if (!await instances.IsAvailableAsync(replica, clientId))
                return (false, "Replica недоступна, синхронизация невозможна", null);

            log($"[{InstanceManager.Timestamp()}] Client {clientId} → Recovery → START | Primary восстановлен");

            var replicaSnapshot = await ReadAsync(replica.ConnectionString);
            var before = Compare(await ReadAsync(primary.ConnectionString), replicaSnapshot);
            log($"[{InstanceManager.Timestamp()}] Client {clientId} → Recovery → COMPARE | расхождений: {before.Count}");

            log($"[{InstanceManager.Timestamp()}] Client {clientId} → Synchronization → START | Replica → Primary");
            await WriteAsync(primary.ConnectionString, replicaSnapshot);
            log($"[{InstanceManager.Timestamp()}] Client {clientId} → Synchronization → SUCCESS");

            var after = Compare(await ReadAsync(primary.ConnectionString), replicaSnapshot);
            instances.ReplicaInSync = after.Count == 0;
            log($"[{InstanceManager.Timestamp()}] Client {clientId} → Recovery → VERIFY | расхождений после синхронизации: {after.Count}");

            var report = new RecoveryReport(before, after, after.Count == 0);
            return (true, "Primary восстановлен и синхронизирован с Replica", report);
        }
        catch (NpgsqlException exception)
        {
            log($"[{InstanceManager.Timestamp()}] Client {clientId} → Synchronization → FAIL | {exception.Message}");
            return (false, $"Ошибка синхронизации: {exception.Message}", null);
        }
        finally
        {
            gate.Release();
        }
    }

    
    public async Task<ConsistencyReport> CompareAsync(int clientId)
    {
        var replica = instances.Replica ?? throw new NoInstanceAvailableException("Резервная копия не настроена");
        await instances.RequireAsync(instances.Primary, clientId);
        await instances.RequireAsync(replica, clientId);

        var differences = Compare(await ReadAsync(instances.Primary.ConnectionString),
            await ReadAsync(replica.ConnectionString));
        return new ConsistencyReport(differences, differences.Count == 0);
    }

    public static List<string> Compare(Snapshot primary, Snapshot replica)
    {
        var differences = new List<string>();
        CompareTable("products", primary.Products, replica.Products, p => p.Id,
            p => $"stock={p.Stock}, price={p.Price}", differences);
        CompareTable("orders", primary.Orders, replica.Orders, o => o.Id,
            o => $"status={o.Status}, total={o.Total}", differences);
        CompareTable("order_items", primary.Items, replica.Items, i => i.Id,
            i => $"order={i.OrderId}, product={i.ProductId}, qty={i.Quantity}", differences);
        CompareTable("payments", primary.Payments, replica.Payments, p => p.Id,
            p => $"order={p.OrderId}, amount={p.Amount}, status={p.Status}", differences);
        return differences;
    }

    private static void CompareTable<T>(string table, List<T> primary, List<T> replica,
        Func<T, int> key, Func<T, string> describe, List<string> differences)
    {
        var primaryRows = primary.ToDictionary(key);
        var replicaRows = replica.ToDictionary(key);

        foreach (var id in primaryRows.Keys.Union(replicaRows.Keys).OrderBy(id => id))
        {
            if (!replicaRows.TryGetValue(id, out var replicaRow))
                differences.Add($"{table}[{id}]: есть в Primary ({describe(primaryRows[id])}), отсутствует в Replica");
            else if (!primaryRows.TryGetValue(id, out var primaryRow))
                differences.Add($"{table}[{id}]: есть в Replica ({describe(replicaRow)}), отсутствует в Primary");
            else if (!Equals(primaryRow, replicaRow))
                differences.Add($"{table}[{id}]: Primary = {describe(primaryRow)}, Replica = {describe(replicaRow)}");
        }
    }

    public static async Task<Snapshot> ReadAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var products = new List<ProductRow>();
        await using (var command = new NpgsqlCommand("SELECT id, name, price, stock FROM products ORDER BY id", connection))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                products.Add(new ProductRow(reader.GetInt32(0), reader.GetString(1), reader.GetDecimal(2), reader.GetInt32(3)));

        var orders = new List<OrderRow>();
        await using (var command = new NpgsqlCommand("SELECT id, customer_id, status, total FROM orders ORDER BY id", connection))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                orders.Add(new OrderRow(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetDecimal(3)));

        var items = new List<ItemRow>();
        await using (var command = new NpgsqlCommand(
                         "SELECT id, order_id, product_id, quantity, price FROM order_items ORDER BY id", connection))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                items.Add(new ItemRow(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2),
                    reader.GetInt32(3), reader.GetDecimal(4)));

        var payments = new List<PaymentRow>();
        await using (var command = new NpgsqlCommand("SELECT id, order_id, amount, status FROM payments ORDER BY id", connection))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                payments.Add(new PaymentRow(reader.GetInt32(0), reader.GetInt32(1), reader.GetDecimal(2), reader.GetString(3)));

        return new Snapshot(products, orders, items, payments);
    }

    
    public static async Task WriteAsync(string connectionString, Snapshot snapshot)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await ExecuteAsync(connection, transaction, "DELETE FROM payments");
        await ExecuteAsync(connection, transaction, "DELETE FROM order_items");
        await ExecuteAsync(connection, transaction, "DELETE FROM orders");

        foreach (var product in snapshot.Products)
            await ExecuteAsync(connection, transaction,
                "INSERT INTO products (id, name, price, stock) VALUES (@id, @name, @price, @stock) " +
                "ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name, price = EXCLUDED.price, stock = EXCLUDED.stock",
                ("id", product.Id), ("name", product.Name), ("price", product.Price), ("stock", product.Stock));

        foreach (var order in snapshot.Orders)
            await ExecuteAsync(connection, transaction,
                "INSERT INTO orders (id, customer_id, status, total) VALUES (@id, @customerId, @status, @total)",
                ("id", order.Id), ("customerId", order.CustomerId), ("status", order.Status), ("total", order.Total));

        foreach (var item in snapshot.Items)
            await ExecuteAsync(connection, transaction,
                "INSERT INTO order_items (id, order_id, product_id, quantity, price) " +
                "VALUES (@id, @orderId, @productId, @quantity, @price)",
                ("id", item.Id), ("orderId", item.OrderId), ("productId", item.ProductId),
                ("quantity", item.Quantity), ("price", item.Price));

        foreach (var payment in snapshot.Payments)
            await ExecuteAsync(connection, transaction,
                "INSERT INTO payments (id, order_id, amount, status) VALUES (@id, @orderId, @amount, @status)",
                ("id", payment.Id), ("orderId", payment.OrderId), ("amount", payment.Amount), ("status", payment.Status));

        
        foreach (var table in new[] { "orders", "order_items", "payments" })
            await ExecuteAsync(connection, transaction,
                $"SELECT setval(pg_get_serial_sequence('{table}', 'id'), COALESCE(MAX(id), 1), MAX(id) IS NOT NULL) FROM {table}");

        await transaction.CommitAsync();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }
}
