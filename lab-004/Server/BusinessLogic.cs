using Npgsql;
using OnlineStore.Shared;

namespace OnlineStore.Server;

public sealed class BusinessLogic : IRequestProcessor
{
    public static readonly TimeSpan DefaultProcessingDelay = TimeSpan.FromMilliseconds(500);

    
    public static readonly TimeSpan DefaultRaceWindow = TimeSpan.FromMilliseconds(300);

    private readonly string connectionString;
    private readonly TimeSpan processingDelay;
    private readonly TimeSpan timeResponseDelay;
    private readonly TimeSpan raceWindow;
    private readonly Action<string> log;

    
    private readonly object stockLock = new();

    public BusinessLogic(
        string connectionString,
        TimeSpan? processingDelay = null,
        TimeSpan? timeResponseDelay = null,
        TimeSpan? raceWindow = null,
        Action<string>? log = null)
    {
        this.connectionString = connectionString;
        this.processingDelay = processingDelay ?? DefaultProcessingDelay;
        this.timeResponseDelay = timeResponseDelay ?? TimeSpan.Zero;
        this.raceWindow = raceWindow ?? DefaultRaceWindow;
        this.log = log ?? (_ => { });
    }

    public async Task<Response> HandleAsync(Request? request, int clientId)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Operation))
            return new(false, "Некорректный запрос");

        try
        {
            return request.Operation switch
            {
                "GetProducts" => await GetProductsAsync(),
                "GetState" => await GetStateAsync(),
                "Reset" => await ResetDataAsync(),
                "CreateOrder" => await CreateOrderAsync(request.Data, clientId),
                "CancelOrder" => await CancelOrderAsync(request.Data, clientId),
                "PayOrder" => await PayOrderAsync(request.Data, clientId),
                "TIME_REQUEST" => await GetServerTimeAsync(),
                _ => new(false, "Неизвестная операция")
            };
        }
        catch (NpgsqlException exception)
        {
            log($"[{Timestamp()}] Client {clientId} → DB ERROR | {exception.Message}");
            return new(false, $"Ошибка базы данных: {exception.Message}");
        }
        catch (Exception exception)
        {
            log($"[{Timestamp()}] Client {clientId} → ERROR | {exception.GetType().Name}: {exception.Message}");
            return new(false, $"Ошибка обработки запроса: {exception.Message}");
        }
    }

    
    private async Task<Response> GetProductsAsync()
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var products = new List<Product>();
        await using var command = new NpgsqlCommand("SELECT id, name, price, stock FROM products ORDER BY id", connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            products.Add(new Product(reader.GetInt32(0), reader.GetString(1), reader.GetDecimal(2))
            {
                Stock = reader.GetInt32(3)
            });

        return new(true, "Товары получены", products);
    }

    
    private async Task<Response> GetStateAsync() =>
        new(true, "Состояние БД", await ReadStateAsync(), "DatabaseState");

    private async Task<Response> ResetDataAsync()
    {
        Db.ResetData(connectionString, log);
        return new(true, "Данные эксперимента сброшены", await ReadStateAsync(), "DataReset");
    }

    private async Task<DatabaseState> ReadStateAsync()
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var products = new List<Product>();
        await using (var command = new NpgsqlCommand("SELECT id, name, price, stock FROM products ORDER BY id", connection))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                products.Add(new Product(reader.GetInt32(0), reader.GetString(1), reader.GetDecimal(2))
                {
                    Stock = reader.GetInt32(3)
                });

        var orders = new List<Order>();
        await using (var command = new NpgsqlCommand(
                         "SELECT id, customer_id, status, total FROM orders ORDER BY id", connection))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                orders.Add(new Order
                {
                    Id = reader.GetInt32(0),
                    CustomerId = reader.GetInt32(1),
                    Status = reader.GetString(2),
                    Total = reader.GetDecimal(3)
                });

        var payments = new List<Payment>();
        await using (var command = new NpgsqlCommand(
                         "SELECT id, order_id, amount, status, created_at FROM payments ORDER BY id", connection))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                payments.Add(new Payment
                {
                    Id = reader.GetInt32(0),
                    OrderId = reader.GetInt32(1),
                    Amount = reader.GetDecimal(2),
                    Status = reader.GetString(3),
                    CreatedAt = reader.GetDateTime(4).ToLocalTime()
                });

        return new DatabaseState(products, orders, payments);
    }

    private async Task<Response> GetServerTimeAsync()
    {
        var serverTime = DateTime.UtcNow;
        if (timeResponseDelay > TimeSpan.Zero)
        {
            log($"[{Timestamp()}] TIME_REQUEST | Искусственная задержка ответа: {timeResponseDelay.TotalMilliseconds:F0} мс");
            await Task.Delay(timeResponseDelay);
        }

        log($"[{Timestamp()}] TIME_RESPONSE | ServerTime = {serverTime:HH:mm:ss.fff}");
        return new(true, "Время сервера", new TimeInfo(serverTime), "TimeResponse");
    }

    private async Task<Response> CreateOrderAsync(Dictionary<string, string>? data, int clientId)
    {
        if (!TryGetInt(data, "customerId", out var customerId) ||
            !TryGetInt(data, "productId", out var productId) ||
            !TryGetInt(data, "quantity", out var quantity) || quantity <= 0)
            return new(false, "Некорректные входные данные");

        if (!TryGetInt(data, "failAt", out var failAt))
            failAt = 0;
        if (failAt is < 0 or > 2)
            return new(false, "Некорректный параметр failAt (допустимы 0, 1, 2)");

        var useLock = TryGetBool(data, "useLock", true);
        var useTransaction = TryGetBool(data, "useTransaction", true);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        if (!CustomerExists(connection, customerId))
            return new(false, "Покупатель не найден");

        
        if (processingDelay > TimeSpan.Zero)
            await Task.Delay(processingDelay);

        log($"[{Timestamp()}] Client {clientId} → START Reserve | Product {productId}, Quantity {quantity}" +
            $" | lock={useLock}, tx={useTransaction}, failAt={failAt}");

        if (!useLock)
            return ReserveWithoutLock(connection, clientId, customerId, productId, quantity, useTransaction, failAt);

        Response result;
        lock (stockLock)
        {
            log($"[{Timestamp()}] Client {clientId} → LOCK | вход в критическую секцию");
            result = ReserveCore(connection, clientId, customerId, productId, quantity, useTransaction, failAt);
        }
        log($"[{Timestamp()}] Client {clientId} → UNLOCK | выход из критической секции");
        return result;
    }

    
    
    
    
    private Response ReserveCore(NpgsqlConnection connection, int clientId, int customerId,
        int productId, int quantity, bool useTransaction, int failAt)
    {
        var product = ReadProduct(connection, productId);
        if (product is null)
        {
            log($"[{Timestamp()}] Client {clientId} → FAIL | Товар {productId} не найден");
            return new(false, "Товар не найден");
        }

        log($"[{Timestamp()}] Client {clientId} → Check resource | Available = {product.Value.Stock}");

        if (product.Value.Stock < quantity)
        {
            log($"[{Timestamp()}] Client {clientId} → FAIL | Available = {product.Value.Stock} < {quantity}");
            return new(false, "Недостаточно товара на складе");
        }

        return ExecuteCompositeOperation(connection, clientId, customerId, productId, quantity,
            product.Value.Price, product.Value.Stock - quantity, useTransaction, failAt);
    }

    private Response ReserveWithoutLock(NpgsqlConnection connection, int clientId, int customerId,
        int productId, int quantity, bool useTransaction, int failAt)
    {
        var product = ReadProduct(connection, productId);
        if (product is null)
        {
            log($"[{Timestamp()}] Client {clientId} → FAIL | Товар {productId} не найден (без lock)");
            return new(false, "Товар не найден");
        }

        log($"[{Timestamp()}] Client {clientId} → Check resource (без lock) | Available = {product.Value.Stock}");

        if (product.Value.Stock < quantity)
        {
            log($"[{Timestamp()}] Client {clientId} → FAIL (без lock) | Available = {product.Value.Stock} < {quantity}");
            return new(false, "Недостаточно товара на складе");
        }

        if (raceWindow > TimeSpan.Zero)
            Thread.Sleep(raceWindow);

        return ExecuteCompositeOperation(connection, clientId, customerId, productId, quantity,
            product.Value.Price, product.Value.Stock - quantity, useTransaction, failAt);
    }

    
    private Response ExecuteCompositeOperation(NpgsqlConnection connection, int clientId, int customerId,
        int productId, int quantity, decimal price, int newStock, bool useTransaction, int failAt) =>
        useTransaction
            ? ExecuteWithTransaction(connection, clientId, customerId, productId, quantity, price, newStock, failAt)
            : ExecuteWithoutTransaction(connection, clientId, customerId, productId, quantity, price, newStock, failAt);

    
    private Response ExecuteWithoutTransaction(NpgsqlConnection connection, int clientId, int customerId,
        int productId, int quantity, decimal price, int newStock, int failAt)
    {
        var total = price * quantity;
        try
        {
            var orderId = InsertOrder(connection, transaction: null, customerId, total);
            InsertOrderItem(connection, transaction: null, orderId, productId, quantity, price);
            log($"[{Timestamp()}] Client {clientId} → Operation 1 (CreateOrder) → SUCCESS | OrderId = {orderId}");

            if (failAt == 1)
                throw new SimulatedOperationException("ошибка между Операцией 1 и Операцией 2");

            UpdateStock(connection, transaction: null, productId, newStock);
            log($"[{Timestamp()}] Client {clientId} → Operation 2 (UpdateStock) → SUCCESS | Available = {newStock}");

            if (failAt == 2)
                throw new SimulatedOperationException("ошибка после Операции 2 (перед COMMIT)");

            log($"[{Timestamp()}] Client {clientId} → SUCCESS | Available = {newStock} | транзакция НЕ использовалась");
            return new(true, "Заказ создан",
                BuildOrder(orderId, customerId, productId, quantity, price), "OrderCreated");
        }
        catch (SimulatedOperationException exception)
        {
            log($"[{Timestamp()}] Client {clientId} → ERROR | {exception.Message}");
            log($"[{Timestamp()}] Client {clientId} → Без транзакции откат невозможен — состояние БД ЧАСТИЧНО ИЗМЕНЕНО");
            return new(false,
                $"Искусственная ошибка: {exception.Message}. Транзакция не использовалась — частичные изменения сохранены в БД");
        }
        catch (NpgsqlException exception)
        {
            log($"[{Timestamp()}] Client {clientId} → DB ERROR | {exception.Message} | без транзакции — частичные изменения сохранены");
            return new(false,
                $"Ошибка БД: {exception.Message}. Транзакция не использовалась — состояние БД ЧАСТИЧНО ИЗМЕНЕНО");
        }
    }
    
    private Response ExecuteWithTransaction(NpgsqlConnection connection, int clientId, int customerId,
        int productId, int quantity, decimal price, int newStock, int failAt)
    {
        var total = price * quantity;
        using var transaction = connection.BeginTransaction();
        try
        {
            var orderId = InsertOrder(connection, transaction, customerId, total);
            InsertOrderItem(connection, transaction, orderId, productId, quantity, price);
            log($"[{Timestamp()}] Client {clientId} → BEGIN | Operation 1 (CreateOrder) → SUCCESS | OrderId = {orderId}");

            if (failAt == 1)
                throw new SimulatedOperationException("ошибка между Операцией 1 и Операцией 2");

            UpdateStock(connection, transaction, productId, newStock);
            log($"[{Timestamp()}] Client {clientId} → Operation 2 (UpdateStock) → SUCCESS | Available = {newStock}");

            if (failAt == 2)
                throw new SimulatedOperationException("ошибка после Операции 2 (перед COMMIT)");

            transaction.Commit();
            log($"[{Timestamp()}] Client {clientId} → COMMIT | изменения подтверждены | Available = {newStock}");
            log($"[{Timestamp()}] Client {clientId} → SUCCESS | Available = {newStock} | транзакция подтверждена");
            return new(true, "Заказ создан, транзакция подтверждена (COMMIT)",
                BuildOrder(orderId, customerId, productId, quantity, price), "OrderCreated");
        }
        catch (SimulatedOperationException exception)
        {
            transaction.Rollback();
            log($"[{Timestamp()}] Client {clientId} → ERROR | {exception.Message}");
            log($"[{Timestamp()}] Client {clientId} → ROLLBACK | изменения отменены — состояние БД как до BEGIN");
            return new(false,
                $"Искусственная ошибка: {exception.Message}. Выполнен ROLLBACK — изменения отменены");
        }
        catch (NpgsqlException exception)
        {
            var rollbackMessage = TryRollback(transaction);
            log($"[{Timestamp()}] Client {clientId} → DB ERROR | {exception.Message} | {rollbackMessage}");
            return new(false, $"Ошибка БД внутри транзакции: {exception.Message}. {rollbackMessage}");
        }
    }

    
    private async Task<Response> CancelOrderAsync(Dictionary<string, string>? data, int clientId)
    {
        if (!TryGetInt(data, "orderId", out var orderId))
            return new(false, "Некорректные входные данные");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        Response result;
        lock (stockLock)
        {
            log($"[{Timestamp()}] Client {clientId} → LOCK | CancelOrder {orderId}");
            result = CancelOrderCore(connection, clientId, orderId);
        }
        log($"[{Timestamp()}] Client {clientId} → UNLOCK | CancelOrder {orderId}");
        return result;
    }

    private Response CancelOrderCore(NpgsqlConnection connection, int clientId, int orderId)
    {
        using var transaction = connection.BeginTransaction();
        try
        {
            var order = ReadOrder(connection, transaction, orderId);
            if (order is null)
            {
                log($"[{Timestamp()}] Client {clientId} → FAIL | Заказ {orderId} не найден");
                return new(false, "Заказ не найден");
            }
            if (order.Value.Status == "Cancelled")
                return new(false, "Заказ уже отменен");
            if (IsPaid(connection, transaction, orderId))
                return new(false, "Оплаченный заказ нельзя отменить");

            foreach (var (productId, itemQuantity) in ReadOrderItems(connection, transaction, orderId))
                AddStock(connection, transaction, productId, itemQuantity);

            SetOrderStatus(connection, transaction, orderId, "Cancelled");
            transaction.Commit();
            log($"[{Timestamp()}] Client {clientId} → COMMIT | Заказ {orderId} отменён, товары возвращены на склад");
            return new(true, "Заказ отменен, товары возвращены на склад", null, "OrderCancelled");
        }
        catch (NpgsqlException exception)
        {
            var rollbackMessage = TryRollback(transaction);
            log($"[{Timestamp()}] Client {clientId} → DB ERROR | {exception.Message} | {rollbackMessage}");
            return new(false, $"Ошибка БД: {exception.Message}. {rollbackMessage}");
        }
    }

    
    private async Task<Response> PayOrderAsync(Dictionary<string, string>? data, int clientId)
    {
        if (!TryGetInt(data, "orderId", out var orderId))
            return new(false, "Некорректные входные данные");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        using var transaction = connection.BeginTransaction();
        try
        {
            var order = ReadOrder(connection, transaction, orderId);
            if (order is null)
            {
                log($"[{Timestamp()}] Client {clientId} → FAIL | Заказ {orderId} не найден");
                return new(false, "Заказ не найден");
            }
            if (order.Value.Status == "Cancelled")
                return new(false, "Нельзя оплатить отмененный заказ");
            if (IsPaid(connection, transaction, orderId))
                return new(false, "Заказ уже оплачен");

            var paymentId = InsertPayment(connection, transaction, orderId, order.Value.Total);
            SetOrderStatus(connection, transaction, orderId, "Paid");
            transaction.Commit();
            log($"[{Timestamp()}] Client {clientId} → COMMIT | Payment {paymentId} для заказа {orderId}");

            return new(true, "Заказ оплачен",
                new Payment
                {
                    Id = paymentId,
                    OrderId = orderId,
                    Amount = order.Value.Total,
                    Status = "Completed",
                    CreatedAt = DateTime.UtcNow
                }, "PaymentCompleted");
        }
        catch (NpgsqlException exception)
        {
            var rollbackMessage = TryRollback(transaction);
            log($"[{Timestamp()}] Client {clientId} → DB ERROR | {exception.Message} | {rollbackMessage}");
            return new(false, $"Ошибка БД: {exception.Message}. {rollbackMessage}");
        }
    }

    
    private string TryRollback(NpgsqlTransaction transaction)
    {
        try
        {
            transaction.Rollback();
            log($"[{Timestamp()}] ROLLBACK | изменения отменены — состояние БД как до BEGIN");
            return "Выполнен ROLLBACK";
        }
        catch (NpgsqlException exception)
        {
            log($"[{Timestamp()}] ROLLBACK НЕ ВЫПОЛНЕН | {exception.Message}");
            return $"ROLLBACK не выполнен: {exception.Message}";
        }
    }

    private static bool IsPaid(NpgsqlConnection connection, NpgsqlTransaction? transaction, int orderId)
    {
        using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM payments WHERE order_id = @orderId AND status = 'Completed')", connection);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("orderId", orderId);
        return (bool)command.ExecuteScalar()!;
    }

    private readonly record struct OrderRow(int CustomerId, string Status, decimal Total);

    private static OrderRow? ReadOrder(NpgsqlConnection connection, NpgsqlTransaction? transaction, int orderId)
    {
        using var command = new NpgsqlCommand(
            "SELECT customer_id, status, total FROM orders WHERE id = @id", connection);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("id", orderId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new OrderRow(reader.GetInt32(0), reader.GetString(1), reader.GetDecimal(2))
            : null;
    }

    private static List<(int ProductId, int Quantity)> ReadOrderItems(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, int orderId)
    {
        using var command = new NpgsqlCommand(
            "SELECT product_id, quantity FROM order_items WHERE order_id = @orderId", connection);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("orderId", orderId);
        using var reader = command.ExecuteReader();

        var items = new List<(int, int)>();
        while (reader.Read())
            items.Add((reader.GetInt32(0), reader.GetInt32(1)));
        return items;
    }

    private static void SetOrderStatus(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, int orderId, string status)
    {
        using var command = new NpgsqlCommand("UPDATE orders SET status = @status WHERE id = @id", connection);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("id", orderId);
        command.ExecuteNonQuery();
    }

    private static int InsertPayment(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, int orderId, decimal amount)
    {
        using var command = new NpgsqlCommand(
            "INSERT INTO payments (order_id, amount, status) VALUES (@orderId, @amount, 'Completed') RETURNING id",
            connection);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("orderId", orderId);
        command.Parameters.AddWithValue("amount", amount);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private readonly record struct ProductRow(decimal Price, int Stock);

    
    private static ProductRow? ReadProduct(NpgsqlConnection connection, int productId)
    {
        using var command = new NpgsqlCommand("SELECT price, stock FROM products WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", productId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new ProductRow(reader.GetDecimal(0), reader.GetInt32(1)) : null;
    }

    private static bool CustomerExists(NpgsqlConnection connection, int customerId)
    {
        using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM customers WHERE id = @id)", connection);
        command.Parameters.AddWithValue("id", customerId);
        return (bool)command.ExecuteScalar()!;
    }

    
    private static int InsertOrder(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, int customerId, decimal total)
    {
        using var command = new NpgsqlCommand(
            "INSERT INTO orders (customer_id, status, total) VALUES (@customerId, 'Created', @total) RETURNING id",
            connection);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("customerId", customerId);
        command.Parameters.AddWithValue("total", total);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    
    private static void InsertOrderItem(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        int orderId, int productId, int quantity, decimal price)
    {
        using var command = new NpgsqlCommand(
            "INSERT INTO order_items (order_id, product_id, quantity, price) VALUES (@orderId, @productId, @quantity, @price)",
            connection);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("orderId", orderId);
        command.Parameters.AddWithValue("productId", productId);
        command.Parameters.AddWithValue("quantity", quantity);
        command.Parameters.AddWithValue("price", price);
        command.ExecuteNonQuery();
    }

    
    
    
    
    private static void UpdateStock(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, int productId, int newStock)
    {
        using var command = new NpgsqlCommand("UPDATE products SET stock = @stock WHERE id = @id", connection);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("stock", newStock);
        command.Parameters.AddWithValue("id", productId);
        command.ExecuteNonQuery();
    }

    
    private static void AddStock(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, int productId, int quantity)
    {
        using var command = new NpgsqlCommand(
            "UPDATE products SET stock = stock + @quantity WHERE id = @id", connection);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("quantity", quantity);
        command.Parameters.AddWithValue("id", productId);
        command.ExecuteNonQuery();
    }

    private static Order BuildOrder(int orderId, int customerId, int productId, int quantity, decimal price) =>
        new()
        {
            Id = orderId,
            CustomerId = customerId,
            Items = [new OrderItem { ProductId = productId, Quantity = quantity, Price = price }],
            Total = price * quantity,
            Status = "Created"
        };

    private static bool TryGetInt(Dictionary<string, string>? data, string key, out int value)
    {
        value = 0;
        return data is not null && data.TryGetValue(key, out var rawValue) && int.TryParse(rawValue, out value);
    }

    private static bool TryGetBool(Dictionary<string, string>? data, string key, bool defaultValue)
    {
        if (data is not null && data.TryGetValue(key, out var rawValue))
        {
            if (bool.TryParse(rawValue, out var value))
                return value;
            if (rawValue == "1")
                return true;
            if (rawValue == "0")
                return false;
        }
        return defaultValue;
    }

    private static string Timestamp() => DateTime.Now.ToString("HH:mm:ss.fff");
}


public sealed class SimulatedOperationException(string message) : Exception(message);
