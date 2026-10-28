using OnlineStore.Client;
using OnlineStore.Shared;

if (args.Length > 0 && args[0] == "scenario")
{
    await ScenarioMode.RunAsync(args.Skip(1).ToArray());
    return;
}
if (args.Length > 0 && args[0] == "pubsub")
{
    await PubSubScenarios.RunAsync(new Client(), args.Skip(1).FirstOrDefault());
    return;
}
if (args.Length > 0 && args[0] == "cluster")
{
    await ClusterScenarios.RunAsync(new Client());
    return;
}
if (args.Length > 0 && args[0] == "send")
{
    await ScenarioMode.SendAsync(args.Skip(1).ToArray());
    return;
}

var client = new Client();

Console.WriteLine("=== Интернет-магазин (ЛР №6: Pub/Sub поверх репликации ЛР №5) ===");

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
    Console.WriteLine("10 - Состояние экземпляров (Primary / Replica)");
    Console.WriteLine("11 - Сравнить Primary и Replica");
    Console.WriteLine("12 - Имитировать отказ Primary (вкл/выкл)");
    Console.WriteLine("13 - Восстановить Primary и синхронизировать с Replica");
    Console.WriteLine("14 - Эксперимент: репликация (3 изменения)");
    Console.WriteLine("15 - Эксперимент: отказ Primary → Failover → Recovery");
    Console.WriteLine("16 - Pub/Sub: состояние Broker и подписчиков");
    Console.WriteLine("17 - Pub/Sub: имитировать отказ подписчика (вкл/выкл)");
    Console.WriteLine("18 - Pub/Sub: имитировать отказ Broker (вкл/выкл)");
    Console.WriteLine("19 - Pub/Sub: подключить нового Subscriber (AnalyticsService)");
    Console.WriteLine("20 - Pub/Sub: повторная доставка недоставленных событий (dead letter)");
    Console.WriteLine("21 - Сценарий Pub/Sub: основной (отказ подписчика, новый подписчик)");
    Console.WriteLine("22 - Сценарий Pub/Sub: повторная доставка (Attempt 1 FAIL, 2 FAIL, 3 SUCCESS)");
    Console.WriteLine("23 - Сценарий Pub/Sub: два Broker (failover)");
    Console.WriteLine("24 - Сценарий Pub/Sub: несколько клиентов одновременно");
    Console.WriteLine("25 - Сценарий Pub/Sub: анализ задержек");
    Console.WriteLine("26 - Кластер: состояние узлов (роли, статусы, значения)");
    Console.WriteLine("27 - Кластер: выбор лидера");
    Console.WriteLine("28 - Кластер: отказ / восстановление узла");
    Console.WriteLine("29 - Кластер: heartbeat лидеру (обнаружение отказа)");
    Console.WriteLine("30 - Кластер: предложить значение (голосование)");
    Console.WriteLine("31 - Эксперименты ЛР №7 (выбор лидера и консенсус)");
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
            case "10":
                Print(await client.SendAsync(RequestBuilder.GetInstancesState()));
                break;
            case "11":
                Print(await client.SendAsync(RequestBuilder.Compare()));
                break;
            case "12":
                await SimulateFailureAsync();
                break;
            case "13":
                Print(await client.SendAsync(RequestBuilder.RecoverPrimary()));
                break;
            case "14":
                await ReplicationScenarios.ReplicationAsync();
                break;
            case "15":
                await ReplicationScenarios.FailoverAsync();
                break;
            case "16":
                await PubSubScenarios.ShowStateAsync(client);
                break;
            case "17":
                await PubSubScenarios.ToggleSubscriberAsync(client);
                break;
            case "18":
                Console.Write("Broker (Broker-1 / Broker-2) [Broker-1]: ");
                var brokerName = Console.ReadLine() is { Length: > 0 } rawBroker ? rawBroker : "Broker-1";
                Console.Write("Включить отказ Broker? (д/н, по умолчанию д): ");
                var brokerDown = !Console.ReadLine()?.StartsWith("н", StringComparison.OrdinalIgnoreCase) ?? true;
                Print(await client.SendAsync(RequestBuilder.SetBrokerAvailable(!brokerDown, brokerName)));
                break;
            case "19":
                Print(await client.SendAsync(RequestBuilder.RegisterSubscriber("AnalyticsService")));
                break;
            case "20":
                Console.Write("Подписчик (Logger / StockService / NotificationService / AnalyticsService): ");
                Print(await client.SendAsync(RequestBuilder.RedeliverDeadLetters(Console.ReadLine() ?? "")));
                break;
            case "21":
                await PubSubScenarios.RunAsync(client);
                break;
            case "22":
                await PubSubScenarios.RunAsync(client, "retry");
                break;
            case "23":
                await PubSubScenarios.RunAsync(client, "failover");
                break;
            case "24":
                await PubSubScenarios.RunAsync(client, "clients");
                break;
            case "25":
                await PubSubScenarios.RunAsync(client, "timings");
                break;
            case "26":
                await ClusterScenarios.ShowAsync(client);
                break;
            case "27":
                Print(await client.SendAsync(RequestBuilder.ClusterElect()));
                break;
            case "28":
                Console.Write("Номер узла (1-5): ");
                var nodeId = int.TryParse(Console.ReadLine(), out var parsedNode) ? parsedNode : 0;
                Console.Write("Отказ (f) или восстановление (r)? [f]: ");
                var recover = Console.ReadLine()?.StartsWith("r", StringComparison.OrdinalIgnoreCase) ?? false;
                Print(await client.SendAsync(recover
                    ? RequestBuilder.ClusterRecover(nodeId)
                    : RequestBuilder.ClusterFail(nodeId)));
                break;
            case "29":
                Print(await client.SendAsync(RequestBuilder.ClusterCheckLeader()));
                break;
            case "30":
                Console.Write("Предлагаемое значение [ConfigurationVersion=1]: ");
                var proposal = Console.ReadLine() is { Length: > 0 } rawValue ? rawValue : "ConfigurationVersion=1";
                var decisions = new Dictionary<int, bool>();
                for (var node = 1; node <= 5; node++)
                {
                    Console.Write($"Голос Node {node} (д/н, по умолчанию д): ");
                    decisions[node] = !Console.ReadLine()?.StartsWith("н", StringComparison.OrdinalIgnoreCase) ?? true;
                }
                Print(await client.SendAsync(RequestBuilder.ClusterPropose(proposal, decisions)));
                break;
            case "31":
                await ClusterScenarios.RunAsync(client);
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

async Task SimulateFailureAsync()
{
    Console.Write("Включить отказ Primary? (д/н, по умолчанию д): ");
    var enabled = !Console.ReadLine()?.StartsWith("н", StringComparison.OrdinalIgnoreCase) ?? true;
    Print(await client.SendAsync(RequestBuilder.SimulateFailure("Primary", enabled)));
}

static T? Deserialize<T>(object data) =>
    JsonProtocol.Deserialize<T>(JsonProtocol.Serialize(data));

static void Print(Response response) =>
    Console.WriteLine($"{(response.Success ? "OK" : "FAIL")}: {response.Message}");

