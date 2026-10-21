using System.Diagnostics;
using OnlineStore.Shared;

namespace OnlineStore.Client;

/// <summary>
/// Эксперименты ЛР №6. Без аргумента — основной сценарий (части 2–4); аргументы:
/// retry (повторная доставка), failover (два Broker), clients (несколько клиентов), timings (анализ задержек).
/// </summary>
public static class PubSubScenarios
{
    // StockService работает 3 с, поэтому ждем чуть дольше самого медленного подписчика.
    private static readonly TimeSpan DeliveryWait = TimeSpan.FromSeconds(5);

    public static async Task RunAsync(Client client, string? scenario = null)
    {
        // Данные сбрасываются, чтобы остаток товаров не зависел от предыдущих запусков.
        await client.SendAsync(RequestBuilder.Reset());
        switch (scenario)
        {
            case "retry":
                await RetryAsync(client);
                break;
            case "failover":
                await FailoverAsync(client);
                break;
            case "clients":
                await MultiClientAsync(client);
                break;
            case "timings":
                await TimingsAsync(client);
                break;
            default:
                await MainAsync(client);
                break;
        }
    }

    private static async Task MainAsync(Client client)
    {
        Console.WriteLine("=== Эксперимент Pub/Sub ===");

        Console.WriteLine("\n[1] Одно событие нескольким подписчикам: OrderCreated");
        Print(await client.SendAsync(RequestBuilder.CreateOrder(1, 1, 1)));
        await WaitAsync();
        await ShowStateAsync(client);

        Console.WriteLine("\n[2] Отказ StockService (UNAVAILABLE), публикация нового события");
        Print(await client.SendAsync(RequestBuilder.SetSubscriberAvailable("StockService", false)));
        Print(await client.SendAsync(RequestBuilder.CreateOrder(1, 2, 1)));
        await WaitAsync();
        await ShowStateAsync(client);

        Console.WriteLine("\n[3] Новый Subscriber AnalyticsService подключен, Publisher не меняется");
        Print(await client.SendAsync(RequestBuilder.RegisterSubscriber("AnalyticsService")));
        Print(await client.SendAsync(RequestBuilder.CreateOrder(1, 3, 1)));
        await WaitAsync();
        await ShowStateAsync(client);

        Console.WriteLine("\n[4] StockService восстановлен, повторная доставка недоставленного события");
        Print(await client.SendAsync(RequestBuilder.SetSubscriberAvailable("StockService", true)));
        Print(await client.SendAsync(RequestBuilder.RedeliverDeadLetters("StockService")));
        await WaitAsync();
        await ShowStateAsync(client);
    }

    /// <summary>Attempt 1 → FAIL, Attempt 2 → FAIL, Attempt 3 → SUCCESS: два временных сбоя NotificationService.</summary>
    private static async Task RetryAsync(Client client)
    {
        Console.WriteLine("=== Повторная доставка: два временных сбоя, третья попытка успешна ===");
        Print(await client.SendAsync(RequestBuilder.SetSubscriberFlaky("NotificationService", 2)));
        var startedAt = DateTime.Now;
        var response = await client.SendAsync(RequestBuilder.CreateOrder(1, 1, 1));
        Print(response);
        var orderId = OrderIdOf(response);
        await WaitAsync();
        var timings = await GetTimingsAsync(client);
        foreach (var timing in timings.Where(t => t.EventTime >= startedAt && t.Subscriber == "NotificationService"))
            Console.WriteLine($"NotificationService: попыток {timing.Attempts}, результат {(timing.Success ? "SUCCESS" : "FAIL")}, " +
                              $"общее время {timing.TotalMs:F0} мс");
        await ShowStateAsync(client);
    }

    /// <summary>Отказ Broker-1: Publisher переключается на Broker-2. Отказ обоих — понятная ошибка, сервер работает.</summary>
    private static async Task FailoverAsync(Client client)
    {
        Console.WriteLine("=== Два Broker: failover ===");
        await ShowStateAsync(client, "Broker-1");
        await ShowStateAsync(client, "Broker-2");

        Console.WriteLine("\n[1] Отказ Broker-1, публикация");
        Print(await client.SendAsync(RequestBuilder.SetBrokerAvailable(false, "Broker-1")));
        Print(await client.SendAsync(RequestBuilder.CreateOrder(1, 1, 1)));
        await WaitAsync();
        await ShowStateAsync(client, "Broker-2");

        Console.WriteLine("\n[2] Отказ обоих Broker, публикация");
        Print(await client.SendAsync(RequestBuilder.SetBrokerAvailable(false, "Broker-2")));
        Print(await client.SendAsync(RequestBuilder.CreateOrder(1, 2, 1)));

        Console.WriteLine("\n[3] Восстановление Broker, публикация");
        Print(await client.SendAsync(RequestBuilder.SetBrokerAvailable(true, "Broker-1")));
        Print(await client.SendAsync(RequestBuilder.SetBrokerAvailable(true, "Broker-2")));
        Print(await client.SendAsync(RequestBuilder.CreateOrder(1, 3, 1)));
        await WaitAsync();
        await ShowStateAsync(client, "Broker-1");
    }

    /// <summary>Три клиента одновременно создают заказы, события проходят через один Broker.</summary>
    private static async Task MultiClientAsync(Client client)
    {
        Console.WriteLine("=== Несколько клиентов одновременно ===");
        var startedAt = DateTime.Now;
        var responses = await Task.WhenAll(Enumerable.Range(1, 3)
            .Select(i => new Client().SendAsync(RequestBuilder.CreateOrder(1, 1 + i % 3, 1))));
        foreach (var response in responses)
            Print(response);

        var orderIds = responses.Select(OrderIdOf).ToHashSet();
        // StockService обрабатывает события по одному (3 с каждое), три заказа — 9 с.
        await WaitAsync(TimeSpan.FromSeconds(12));
        var timings = (await GetTimingsAsync(client))
            .Where(t => t.EventType == EventNames.OrderCreated && t.EventTime >= startedAt).ToList();
        Console.WriteLine($"Заказы: {string.Join(", ", orderIds)}; доставок OrderCreated: {timings.Count} (ожидалось {orderIds.Count * 3})");
        foreach (var group in timings.GroupBy(t => t.Subscriber).OrderBy(g => g.Key))
            Console.WriteLine($"  {group.Key,-20} событий: {group.Count()}, успешно: {group.Count(t => t.Success)}");
        await ShowStateAsync(client);
    }

    /// <summary>Сравнение времени доставки, обработки и завершения всех подписчиков для одного события.</summary>
    private static async Task TimingsAsync(Client client)
    {
        Console.WriteLine("=== Анализ задержек ===");
        var startedAt = DateTime.Now;
        var stopwatch = Stopwatch.StartNew();
        var response = await client.SendAsync(RequestBuilder.CreateOrder(1, 1, 1));
        stopwatch.Stop();
        Print(response);
        Console.WriteLine($"Время операции клиента (бизнес-логика + публикация, без ожидания подписчиков): {stopwatch.Elapsed.TotalMilliseconds:F0} мс");

        var orderId = OrderIdOf(response);
        await WaitAsync();
        var timings = (await GetTimingsAsync(client))
            .Where(t => t.EventTime >= startedAt && t.EventType == EventNames.OrderCreated).ToList();

        Console.WriteLine($"{"Подписчик",-20} {"Очередь, мс",12} {"Обработка, мс",14} {"Всего, мс",10} {"Попыток",8}");
        foreach (var t in timings.OrderBy(t => t.TotalMs))
            Console.WriteLine($"{t.Subscriber,-20} {t.QueueWaitMs,12:F0} {t.ProcessingMs,14:F0} {t.TotalMs,10:F0} {t.Attempts,8}");
        if (timings.Count > 0)
            Console.WriteLine($"Завершение всех подписчиков: {timings.Max(t => t.TotalMs):F0} мс " +
                              $"(сумма времён обработки {timings.Sum(t => t.ProcessingMs):F0} мс — подписчики работают параллельно)");
    }

    public static async Task ShowStateAsync(Client client, string? broker = null)
    {
        var response = await client.SendAsync(RequestBuilder.PubSubState(broker));
        if (!response.Success || response.Data is null)
        {
            Print(response);
            return;
        }

        var state = JsonProtocol.Deserialize<PubSubState>(JsonProtocol.Serialize(response.Data));
        if (state is null)
            return;

        Console.WriteLine($"{state.Broker}: {(state.BrokerAvailable ? "AVAILABLE" : "UNAVAILABLE")}");
        Console.WriteLine("Subscribers:");
        foreach (var subscriber in state.Subscribers)
            Console.WriteLine($"  {subscriber.Name,-20} {(subscriber.Available ? "AVAILABLE  " : "UNAVAILABLE")} " +
                              $"подписка: {string.Join(", ", subscriber.EventTypes)} | доставлено: {subscriber.Delivered}, ошибок: {subscriber.Failed}");
        Console.WriteLine($"Dead letters: {state.DeadLetters.Count}");
        foreach (var letter in state.DeadLetters)
            Console.WriteLine($"  {letter.Subscriber}: {letter.EventType} #{letter.EntityId} — {letter.Error}");
    }

    public static async Task ToggleSubscriberAsync(Client client)
    {
        Console.Write("Подписчик (Logger / StockService / NotificationService / AnalyticsService): ");
        var name = Console.ReadLine() ?? "";
        Console.Write("Включить отказ? (д/н, по умолчанию д): ");
        var failure = !Console.ReadLine()?.StartsWith("н", StringComparison.OrdinalIgnoreCase) ?? true;
        Print(await client.SendAsync(RequestBuilder.SetSubscriberAvailable(name, !failure)));
    }

    private static async Task<List<DeliveryTiming>> GetTimingsAsync(Client client)
    {
        var response = await client.SendAsync(RequestBuilder.PubSubTimings());
        return response.Data is null
            ? []
            : JsonProtocol.Deserialize<List<DeliveryTiming>>(JsonProtocol.Serialize(response.Data)) ?? [];
    }

    private static int OrderIdOf(Response response) =>
        response.Data is null
            ? 0
            : JsonProtocol.Deserialize<Order>(JsonProtocol.Serialize(response.Data))?.Id ?? 0;

    private static Task WaitAsync(TimeSpan? wait = null)
    {
        var delay = wait ?? DeliveryWait;
        Console.WriteLine($"Ожидание асинхронной обработки ({delay.TotalSeconds:F0} с)...");
        return Task.Delay(delay);
    }

    private static void Print(Response response) =>
        Console.WriteLine($"{(response.Success ? "OK" : "FAIL")}: {response.Message}");
}
