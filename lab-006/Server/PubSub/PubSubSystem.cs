using OnlineStore.Shared;

namespace OnlineStore.Server;

public sealed class PubSubSystem(Action<string> log, TimeSpan? delayOverride = null) : IDisposable
{
    public IReadOnlyList<Broker> Brokers { get; } =
        [new Broker(log, name: "Broker-1"), new Broker(log, name: "Broker-2")];

    public Broker Broker => Brokers[0];

    public Publisher Publisher { get; private set; } = null!;

    public Action<string> Log { get; } = log;

    public TimeSpan? DelayOverride { get; } = delayOverride;

    public void Start()
    {
        Publisher = new Publisher(Brokers, Log);
        RegisterSubscriber("Logger");
        RegisterSubscriber("StockService");
        RegisterSubscriber("NotificationService");
    }

    public Broker? FindBroker(string? name) =>
        string.IsNullOrWhiteSpace(name) ? Broker : Brokers.FirstOrDefault(broker => broker.Name == name);

    public bool RegisterSubscriber(string name)
    {
        Subscriber? subscriber = name switch
        {
            "Logger" => new LoggerSubscriber(DelayOverride),
            "StockService" => new StockSubscriber(DelayOverride),
            "NotificationService" => new NotificationSubscriber(DelayOverride),
            "AnalyticsService" => new AnalyticsSubscriber(DelayOverride),
            _ => null
        };

        if (subscriber is null)
            return false;

        var registered = false;
        foreach (var broker in Brokers)
            registered |= broker.Subscribe(subscriber);
        return registered;
    }

    public bool SetSubscriberAvailable(string name, bool value) =>
        Brokers.Select(broker => broker.SetSubscriberAvailable(name, value)).ToList().Any(found => found);

    public bool SetSubscriberTransientFailures(string name, int count) =>
        Brokers.Select(broker => broker.SetSubscriberTransientFailures(name, count)).ToList().Any(found => found);

    public int RedeliverDeadLetters(string name) =>
        Brokers.Sum(broker => broker.RedeliverDeadLetters(name));

    public IReadOnlyList<DeliveryTiming> GetTimings() =>
        Brokers.SelectMany(broker => broker.GetTimings()).ToList();

    public void Dispose()
    {
        foreach (var broker in Brokers)
            broker.Dispose();
    }
}
