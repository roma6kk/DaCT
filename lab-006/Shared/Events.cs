namespace OnlineStore.Shared;

public static class EventNames
{
    public const string OrderCreated = "OrderCreated";
    public const string OrderCancelled = "OrderCancelled";
    public const string PaymentCompleted = "PaymentCompleted";

    public static readonly string[] All = [OrderCreated, OrderCancelled, PaymentCompleted];
}

public sealed record DomainEvent(
    string EventType,
    int EntityId,
    int ClientId,
    DateTime Timestamp,
    Dictionary<string, string>? Data = null);

public sealed record SubscriberInfo(string Name, List<string> EventTypes, bool Available, int Delivered, int Failed);

public sealed record DeadLetterInfo(string Subscriber, string EventType, int EntityId, string Error);

public sealed record PubSubState(bool BrokerAvailable, List<SubscriberInfo> Subscribers, List<DeadLetterInfo> DeadLetters, string Broker = "Broker-1");

public sealed record DeliveryTiming(
    string Broker,
    string Subscriber,
    string EventType,
    int EntityId,
    double QueueWaitMs,
    double ProcessingMs,
    double TotalMs,
    bool Success,
    int Attempts,
    DateTime EventTime = default);
