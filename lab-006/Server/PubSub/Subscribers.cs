using System.Collections.Concurrent;
using OnlineStore.Shared;

namespace OnlineStore.Server;

public abstract class Subscriber
{
    private volatile bool available = true;
    private int transientFailures;

    protected Subscriber(string name, IReadOnlyCollection<string> eventTypes, TimeSpan processingDelay)
    {
        Name = name;
        EventTypes = eventTypes;
        ProcessingDelay = processingDelay;
    }

    public string Name { get; }

    public IReadOnlyCollection<string> EventTypes { get; }

    public TimeSpan ProcessingDelay { get; }

    public bool Available
    {
        get => available;
        set => available = value;
    }

    public void FailNext(int count) => Volatile.Write(ref transientFailures, Math.Max(0, count));

    public async Task<string> HandleAsync(DomainEvent domainEvent)
    {
        if (!Available)
            throw new SubscriberUnavailableException($"{Name} → UNAVAILABLE");

        while (true)
        {
            var remaining = Volatile.Read(ref transientFailures);
            if (remaining <= 0)
                break;
            if (Interlocked.CompareExchange(ref transientFailures, remaining - 1, remaining) == remaining)
                throw new SubscriberUnavailableException($"{Name} → временный сбой");
        }

        if (ProcessingDelay > TimeSpan.Zero)
            await Task.Delay(ProcessingDelay);

        return await ProcessAsync(domainEvent);
    }

    protected abstract Task<string> ProcessAsync(DomainEvent domainEvent);
}

public sealed class LoggerSubscriber(TimeSpan? processingDelay = null)
    : Subscriber("Logger", EventNames.All, processingDelay ?? TimeSpan.FromSeconds(1))
{
    private readonly ConcurrentQueue<string> journal = new();

    public IReadOnlyCollection<string> Journal => journal.ToArray();

    protected override Task<string> ProcessAsync(DomainEvent domainEvent)
    {
        journal.Enqueue($"{domainEvent.EventType} #{domainEvent.EntityId} (client {domainEvent.ClientId})");
        return Task.FromResult("запись в журнал создана");
    }
}

public sealed class StockSubscriber(TimeSpan? processingDelay = null)
    : Subscriber("StockService", [EventNames.OrderCreated], processingDelay ?? TimeSpan.FromSeconds(3))
{
    protected override Task<string> ProcessAsync(DomainEvent domainEvent) =>
        Task.FromResult($"резерв по заказу #{domainEvent.EntityId} зафиксирован");
}

public sealed class NotificationSubscriber(TimeSpan? processingDelay = null)
    : Subscriber("NotificationService", [EventNames.OrderCreated, EventNames.PaymentCompleted],
        processingDelay ?? TimeSpan.FromSeconds(2))
{
    protected override Task<string> ProcessAsync(DomainEvent domainEvent) =>
        Task.FromResult($"уведомление о {domainEvent.EventType} отправлено");
}

public sealed class AnalyticsSubscriber(TimeSpan? processingDelay = null)
    : Subscriber("AnalyticsService", EventNames.All, processingDelay ?? TimeSpan.FromMilliseconds(500))
{
    private readonly ConcurrentDictionary<string, int> counts = new();

    public IReadOnlyDictionary<string, int> Counts => counts;

    protected override Task<string> ProcessAsync(DomainEvent domainEvent)
    {
        counts.AddOrUpdate(domainEvent.EventType, 1, (_, value) => value + 1);
        return Task.FromResult("статистика обновлена");
    }
}
