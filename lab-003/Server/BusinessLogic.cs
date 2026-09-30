using OnlineStore.Shared;

namespace OnlineStore.Server;

public sealed class BusinessLogic : IRequestProcessor
{
    public static readonly TimeSpan DefaultProcessingDelay = TimeSpan.FromSeconds(10);

    private readonly TimeSpan processingDelay;
    private readonly TimeSpan timeResponseDelay;
    private readonly Action<string> log;

    private readonly object stockLock = new();

    public BusinessLogic(TimeSpan? processingDelay = null, TimeSpan? timeResponseDelay = null, Action<string>? log = null)
    {
        this.processingDelay = processingDelay ?? DefaultProcessingDelay;
        this.timeResponseDelay = timeResponseDelay ?? TimeSpan.Zero;
        this.log = log ?? (_ => { });
    }

    private readonly List<Product> products =
    [
        new Product(1, "Ноутбук", 85000) { Stock = 5 },
        new Product(2, "Смартфон", 55000) { Stock = 8 },
        new Product(3, "Наушники", 7000) { Stock = 12 },
        new Product(4, "Планшет", 30000) { Stock = 1 }
    ];

    private readonly List<Customer> customers =
    [new Customer { Id = 1, Name = "Иван Иванов" }];

    private readonly List<Order> orders = [];
    private readonly List<Payment> payments = [];
    private int nextOrderId;
    private int nextPaymentId;

    public IReadOnlyList<Product> Products => products;

    public async Task<Response> HandleAsync(Request? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Operation))
            return new(false, "Некорректный запрос");

        return request.Operation switch
        {
            "GetProducts" => GetProducts(),
            "CreateOrder" => await CreateOrderAsync(request.Data),
            "CancelOrder" => CancelOrder(request.Data),
            "PayOrder" => PayOrder(request.Data),
            "TIME_REQUEST" => await GetServerTimeAsync(),
            _ => new(false, "Неизвестная операция")
        };
    }

    private Response GetProducts() => new(true, "Товары получены", products);

    private async Task<Response> CreateOrderAsync(Dictionary<string, string>? data)
    {
        if (!TryGetInt(data, "customerId", out var customerId) ||
            !TryGetInt(data, "productId", out var productId) ||
            !TryGetInt(data, "quantity", out var quantity) || quantity <= 0)
            return new(false, "Некорректные входные данные");

        if (!customers.Any(customer => customer.Id == customerId))
            return new(false, "Покупатель не найден");

        var product = products.FirstOrDefault(item => item.Id == productId);
        if (product is null)
            return new(false, "Товар не найден");


        await Task.Delay(processingDelay);

        var clientId = Environment.CurrentManagedThreadId;
        log($"[{Timestamp()}] Client {clientId} → START Reserve | Product {productId}, Quantity {quantity}");

        Order order;
        lock (stockLock)
        {
            log($"[{Timestamp()}] Client {clientId} → LOCK | Available = {product.Stock}");

            if (product.Stock < quantity)
            {
                log($"[{Timestamp()}] Client {clientId} → FAIL | Available = {product.Stock} < {quantity}");
                return new(false, "Недостаточно товара на складе");
            }

            product.Stock -= quantity;

            order = new Order
            {
                Id = ++nextOrderId,
                CustomerId = customerId,
                Items = [new OrderItem { ProductId = productId, Quantity = quantity, Price = product.Price }],
                Total = product.Price * quantity
            };
            orders.Add(order);

            log($"[{Timestamp()}] Client {clientId} → SUCCESS | Available = {product.Stock}");
        }
        log($"[{Timestamp()}] Client {clientId} → UNLOCK");

        return new(true, "Заказ создан", order, "OrderCreated");
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

    private Response CancelOrder(Dictionary<string, string>? data)
    {
        if (!TryGetInt(data, "orderId", out var orderId))
            return new(false, "Некорректные входные данные");

        var order = orders.FirstOrDefault(item => item.Id == orderId);
        if (order is null)
            return new(false, "Заказ не найден");
        if (order.Status == "Cancelled")
            return new(false, "Заказ уже отменен");
        if (IsPaid(order))
            return new(false, "Оплаченный заказ нельзя отменить");

        order.Status = "Cancelled";
        RestoreStock(order);
        return new(true, "Заказ отменен", order, "OrderCancelled");
    }

    private Response PayOrder(Dictionary<string, string>? data)
    {
        if (!TryGetInt(data, "orderId", out var orderId))
            return new(false, "Некорректные входные данные");

        var order = orders.FirstOrDefault(item => item.Id == orderId);
        if (order is null)
            return new(false, "Заказ не найден");
        if (order.Status == "Cancelled")
            return new(false, "Нельзя оплатить отмененный заказ");
        if (IsPaid(order))
            return new(false, "Заказ уже оплачен");

        var payment = new Payment
        {
            Id = ++nextPaymentId,
            OrderId = order.Id,
            Amount = order.Total,
            Status = "Completed",
            CreatedAt = DateTime.UtcNow
        };
        payments.Add(payment);
        order.Status = "Paid";
        return new(true, "Заказ оплачен", payment, "PaymentCompleted");
    }

    private bool IsPaid(Order order) =>
        payments.Any(payment => payment.OrderId == order.Id && payment.Status == "Completed");

    private void RestoreStock(Order order)
    {
        foreach (var item in order.Items)
            products.First(product => product.Id == item.ProductId).Stock += item.Quantity;
    }

    private static bool TryGetInt(Dictionary<string, string>? data, string key, out int value)
    {
        value = 0;
        return data is not null && data.TryGetValue(key, out var rawValue) && int.TryParse(rawValue, out value);
    }

    private static string Timestamp() => DateTime.Now.ToString("HH:mm:ss.fff");
}