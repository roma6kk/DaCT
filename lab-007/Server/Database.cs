using Npgsql;

namespace OnlineStore.Server;

public static class Db
{
    public const string DefaultConnectionString =
        "Host=localhost;Port=5434;Database=onlinestore;Username=store;Password=store";

    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("ONLINESTORE_DB") is { Length: > 0 } connectionString
            ? connectionString
            : DefaultConnectionString;

    
    public const string DefaultReplicaConnectionString =
        "Host=localhost;Port=5435;Database=onlinestore;Username=store;Password=store";

    public static string ReplicaConnectionString =>
        Environment.GetEnvironmentVariable("ONLINESTORE_REPLICA_DB") is { Length: > 0 } connectionString
            ? connectionString
            : DefaultReplicaConnectionString;

    public static readonly (int Id, string Name, decimal Price, int Stock)[] SeedProducts =
    [
        (1, "Ноутбук", 85000m, 5),
        (2, "Смартфон", 55000m, 8),
        (3, "Наушники", 7000m, 12),
        (4, "Планшет", 30000m, 1)
    ];

    public static readonly (int Id, string Name)[] SeedCustomers = [(1, "Иван Иванов")];

    public const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS customers (
            id   SERIAL PRIMARY KEY,
            name TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS products (
            id    SERIAL PRIMARY KEY,
            name  TEXT   NOT NULL,
            price NUMERIC(12,2) NOT NULL CHECK (price >= 0),
            stock INTEGER NOT NULL CHECK (stock >= 0)
        );

        CREATE TABLE IF NOT EXISTS orders (
            id          SERIAL PRIMARY KEY,
            customer_id INTEGER NOT NULL REFERENCES customers(id),
            status      TEXT NOT NULL DEFAULT 'Created',
            total       NUMERIC(12,2) NOT NULL,
            created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE TABLE IF NOT EXISTS order_items (
            id         SERIAL PRIMARY KEY,
            order_id   INTEGER NOT NULL REFERENCES orders(id) ON DELETE CASCADE,
            product_id INTEGER NOT NULL REFERENCES products(id),
            quantity   INTEGER NOT NULL CHECK (quantity > 0),
            price      NUMERIC(12,2) NOT NULL
        );

        CREATE TABLE IF NOT EXISTS payments (
            id         SERIAL PRIMARY KEY,
            order_id   INTEGER NOT NULL REFERENCES orders(id),
            amount     NUMERIC(12,2) NOT NULL CHECK (amount > 0),
            status     TEXT NOT NULL,
            created_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE INDEX IF NOT EXISTS idx_orders_customer ON orders(customer_id);
        CREATE INDEX IF NOT EXISTS idx_items_order     ON order_items(order_id);
        CREATE INDEX IF NOT EXISTS idx_items_product   ON order_items(product_id);
        CREATE INDEX IF NOT EXISTS idx_payments_order  ON payments(order_id);
        """;

    public static void EnsureDatabase(string connectionString, Action<string>? log = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.Database))
            return;

        var databaseName = builder.Database;
        builder.Database = "postgres";

        using var maintenance = new NpgsqlConnection(builder.ConnectionString);
        maintenance.Open();

        using var check = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", maintenance);
        check.Parameters.AddWithValue("name", databaseName);
        if (check.ExecuteScalar() is not null)
            return;

        var quoted = databaseName.Replace("\"", "\"\"");
        using var create = new NpgsqlCommand($"CREATE DATABASE \"{quoted}\"", maintenance);
        create.ExecuteNonQuery();
        log?.Invoke($"[DB] Создана база данных \"{databaseName}\"");
    }

    public static void EnsureSchema(string connectionString, Action<string>? log = null)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using (var schema = new NpgsqlCommand(SchemaSql, connection))
            schema.ExecuteNonQuery();

        using (var count = new NpgsqlCommand("SELECT COUNT(*) FROM products", connection))
            if (Convert.ToInt32(count.ExecuteScalar()) == 0)
                SeedProductRows(connection);

        using (var count = new NpgsqlCommand("SELECT COUNT(*) FROM customers", connection))
            if (Convert.ToInt32(count.ExecuteScalar()) == 0)
                SeedCustomerRows(connection);

        log?.Invoke("[DB] Схема БД готова: таблицы customers, products, orders, order_items, payments");
    }

    public static void ResetData(string connectionString, Action<string>? log = null)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using (var truncate = new NpgsqlCommand(
                   "TRUNCATE TABLE payments, order_items, orders RESTART IDENTITY", connection))
            truncate.ExecuteNonQuery();

        foreach (var (id, _, _, stock) in SeedProducts)
        {
            using var update = new NpgsqlCommand("UPDATE products SET stock = @stock WHERE id = @id", connection);
            update.Parameters.AddWithValue("stock", stock);
            update.Parameters.AddWithValue("id", id);
            update.ExecuteNonQuery();
        }

        log?.Invoke("[DB] Данные эксперимента сброшены: заказы и платежи удалены, остатки восстановлены");
    }

    private static void SeedProductRows(NpgsqlConnection connection)
    {
        foreach (var (id, name, price, stock) in SeedProducts)
        {
            using var command = new NpgsqlCommand(
                "INSERT INTO products (id, name, price, stock) VALUES (@id, @name, @price, @stock)", connection);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("name", name);
            command.Parameters.AddWithValue("price", price);
            command.Parameters.AddWithValue("stock", stock);
            command.ExecuteNonQuery();
        }
    }

    private static void SeedCustomerRows(NpgsqlConnection connection)
    {
        foreach (var (id, name) in SeedCustomers)
        {
            using var command = new NpgsqlCommand("INSERT INTO customers (id, name) VALUES (@id, @name)", connection);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("name", name);
            command.ExecuteNonQuery();
        }
    }
}
