using OnlineStore.Client;
using OnlineStore.Shared;

if (args.Length > 0 && args[0] == "scenario")
{
    await ScenarioMode.RunAsync(args.Skip(1).ToArray());
    return;
}
if (args.Length > 0 && args[0] == "send")
{
    await ScenarioMode.SendAsync(args.Skip(1).ToArray());
    return;
}

var client = new Client();

Console.WriteLine("=== Интернет-магазин (ЛР №4: БД, lock, транзакции) ===");

while (true)
{
    Console.WriteLine();
    Console.WriteLine("1 - Показать товары (чтение из БД)");
    Console.WriteLine("2 - Создать заказ (транзакция COMMIT)");
    Console.WriteLine("3 - Отменить заказ");
    Console.WriteLine("4 - Оплатить заказ");
    Console.WriteLine("5 - Эксперимент: составная операция без/с транзакцией");
    Console.WriteLine("6 - Показать состояние БД (products / orders / payments)");
    Console.WriteLine("7 - Сбросить данные эксперимента");
    Console.WriteLine("8 - Конкурентный эксперимент: 2 клиента, последний товар (lock)");
    Console.WriteLine("9 - Синхронизировать время (TIME_REQUEST)");
    Console.WriteLine("0 - Выход");
    Console.Write("Выберите действие: ");
    var choice = Console.ReadLine();

    try
    {
        switch (choice)
        {
            case "1":
                await ShowProductsAsync();
                break;
            case "2":
                await CreateOrderAsync();
                break;
            case "3":
                await CancelOrderAsync();
                break;
            case "4":
                await PayOrderAsync();
                break;
            case "5":
                await TransactionExperimentAsync();
                break;
            case "6":
                await ShowStateAsync("Состояние БД:");
                break;
            case "7":
                await ResetAsync();
                break;
            case "8":
                await ScenarioMode.RaceAsync();
                break;
            case "9":
                await SyncTimeAsync();
                break;
            case "0":
                return;
        }
    }
    catch (Exception exception)
    {
        Console.WriteLine($"Ошибка: {exception.Message}");
    }
}

async Task ShowProductsAsync()
{
    var response = await client.SendAsync(RequestBuilder.GetProducts());
    Print(response);
    if (response.Success && response.Data is not null)
        foreach (var product in Deserialize<List<Product>>(response.Data) ?? [])
            Console.WriteLine($"{product.Id}. {product.Name} — {product.Price} ₽, на складе: {product.Stock}");
}

async Task CreateOrderAsync()
{
    Console.Write("ID покупателя: ");
    var customerId = int.Parse(Console.ReadLine() ?? "1");
    Console.Write("ID товара: ");
    var productId = int.Parse(Console.ReadLine() ?? "1");
    Console.Write("Количество: ");
    var quantity = int.Parse(Console.ReadLine() ?? "1");
    Console.Write("Выполнить в транзакции? (д/н, по умолчанию д): ");
    var useTransaction = !Console.ReadLine()?.StartsWith("н", StringComparison.OrdinalIgnoreCase) ?? true;

    var response = await client.SendAsync(
        RequestBuilder.CreateOrder(customerId, productId, quantity, useTransaction));
    Print(response);
}

async Task CancelOrderAsync()
{
    Console.Write("ID заказа: ");
    var orderId = int.Parse(Console.ReadLine() ?? "1");
    Print(await client.SendAsync(RequestBuilder.CancelOrder(orderId)));
}

async Task PayOrderAsync()
{
    Console.Write("ID заказа: ");
    var orderId = int.Parse(Console.ReadLine() ?? "1");
    Print(await client.SendAsync(RequestBuilder.PayOrder(orderId)));
}


async Task TransactionExperimentAsync()
{
    Console.Write("ID товара [4]: ");
    var productId = int.Parse(Console.ReadLine() is { Length: > 0 } raw ? raw : "4");
    Console.Write("Количество [1]: ");
    var quantity = int.Parse(Console.ReadLine() is { Length: > 0 } rawQuantity ? rawQuantity : "1");
    Console.Write("Использовать транзакцию? (д/н, по умолчанию д): ");
    var useTransaction = !Console.ReadLine()?.StartsWith("н", StringComparison.OrdinalIgnoreCase) ?? true;
    Console.Write("Ошибка: 0 — нет, 1 — между операциями, 2 — перед COMMIT [0]: ");
    var failAt = int.Parse(Console.ReadLine() is { Length: > 0 } rawFailAt ? rawFailAt : "0");

    await ShowStateAsync("Состояние БД ДО эксперимента:");
    var response = await client.SendAsync(
        RequestBuilder.CreateOrder(1, productId, quantity, useTransaction, useLock: true, failAt));
    Print(response);
    await ShowStateAsync("Состояние БД ПОСЛЕ эксперимента:");
}

async Task ShowStateAsync(string header)
{
    var response = await client.SendAsync(RequestBuilder.GetState());
    if (!response.Success || response.Data is null)
    {
        Print(response);
        return;
    }

    var state = Deserialize<DatabaseState>(response.Data);
    if (state is null)
        return;

    Console.WriteLine(header);
    Console.WriteLine("  products:");
    foreach (var product in state.Products)
        Console.WriteLine($"    id={product.Id} {product.Name} price={product.Price} stock={product.Stock}");
    Console.WriteLine("  orders:");
    if (state.Orders.Count == 0)
        Console.WriteLine("    (нет записей)");
    foreach (var order in state.Orders)
        Console.WriteLine($"    id={order.Id} customer={order.CustomerId} status={order.Status} total={order.Total}");
    Console.WriteLine("  payments:");
    if (state.Payments.Count == 0)
        Console.WriteLine("    (нет записей)");
    foreach (var payment in state.Payments)
        Console.WriteLine($"    id={payment.Id} order={payment.OrderId} amount={payment.Amount} status={payment.Status}");
}

async Task ResetAsync()
{
    Print(await client.SendAsync(RequestBuilder.Reset()));
    await ShowStateAsync("Состояние БД после сброса:");
}

async Task SyncTimeAsync()
{
    var result = await client.SyncTimeAsync();
    if (!result.Success)
    {
        Console.WriteLine($"Синхронизация не удалась: {result.Error}");
        return;
    }

    Console.WriteLine($"Локальное время клиента:   {Client.FormatTime(result.ClientLocalTime)}");
    Console.WriteLine($"Время сервера (ServerTime): {Client.FormatTime(result.ServerTime!.Value)}");
    Console.WriteLine($"RTT: {result.Rtt!.Value.TotalMilliseconds:F1} мс | RTT/2: {result.EstimatedDelay!.Value.TotalMilliseconds:F1} мс");
    Console.WriteLine($"Поправка (Offset): {result.Offset!.Value.TotalMilliseconds:+0.0;-0.0} мс");
    Console.WriteLine($"Скорректированное время: {Client.FormatTime(client.CorrectedTime)}");
}

static T? Deserialize<T>(object data) =>
    JsonProtocol.Deserialize<T>(JsonProtocol.Serialize(data));

static void Print(Response response) =>
    Console.WriteLine($"{(response.Success ? "OK" : "FAIL")}: {response.Message}");

