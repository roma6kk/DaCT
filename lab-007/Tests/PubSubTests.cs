using OnlineStore.Server;
using OnlineStore.Shared;

namespace OnlineStore.Tests;

public sealed class PubSubTests : IDisposable
{
    private readonly PubSubSystem system = new(_ => { }, TimeSpan.Zero);

    public PubSubTests() => system.Start();

    public void Dispose() => system.Dispose();

    private Publisher Publisher => system.Publisher;

    private static DomainEvent Event(string type, int entityId = 1) =>
        new(type, entityId, 1, DateTime.Now);

    private PubSubState State() => system.Broker.GetState();

    private SubscriberInfo Sub(string name) => State().Subscribers.Single(s => s.Name == name);

    private static void WaitUntil(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new Xunit.Sdk.XunitException($"Условие не выполнено за 5 с: {because}");
            Thread.Sleep(20);
        }
    }

    [Fact]
    public void Event_KeepsTypeEntityIdAndTimestamp()
    {
        var timestamp = new DateTime(2026, 10, 28, 10, 15, 32);
        var domainEvent = new DomainEvent(EventNames.OrderCreated, 125, 7, timestamp);

        Assert.Equal("OrderCreated", domainEvent.EventType);
        Assert.Equal(125, domainEvent.EntityId);
        Assert.Equal(7, domainEvent.ClientId);
        Assert.Equal(timestamp, domainEvent.Timestamp);
    }

    [Fact]
    public void Subscribe_SameNameTwice_ReturnsFalse()
    {
        Assert.False(system.RegisterSubscriber("Logger"));
        Assert.False(system.RegisterSubscriber("UnknownService"));
    }

    [Fact]
    public void Publish_DeliversToSubscribedSubscriber()
    {
        var result = Publisher.Publish(Event(EventNames.OrderCancelled));

        Assert.True(result.Success);
        Assert.Equal(1, result.Recipients);
        WaitUntil(() => Sub("Logger").Delivered == 1, "Logger получил OrderCancelled");
    }

    [Fact]
    public void Publish_DoesNotDeliverToUnsubscribedSubscriber()
    {
        var result = Publisher.Publish(Event(EventNames.OrderCancelled));

        Assert.Equal(1, result.Recipients);
        WaitUntil(() => Sub("Logger").Delivered == 1, "доставка Logger");
        Assert.Equal(0, Sub("StockService").Delivered);
        Assert.Equal(0, Sub("NotificationService").Delivered);
    }

    [Fact]
    public void Publish_OneEvent_DeliveredToAllSubscribers()
    {
        var result = Publisher.Publish(Event(EventNames.OrderCreated));

        Assert.Equal(3, result.Recipients);
        WaitUntil(() => Sub("Logger").Delivered == 1 && Sub("StockService").Delivered == 1
                        && Sub("NotificationService").Delivered == 1, "все три подписчика");
    }

    [Fact]
    public void Publish_SeveralEventTypes_RoutedByType()
    {
        Assert.Equal(2, Publisher.Publish(Event(EventNames.PaymentCompleted)).Recipients);
        Assert.Equal(1, Publisher.Publish(Event(EventNames.OrderCancelled)).Recipients);
        Assert.Equal(0, Publisher.Publish(Event("UnknownType")).Recipients);
    }

    [Fact]
    public void Publish_SubscriberFails_BrokerContinuesAndDeadLetterKept()
    {
        system.Broker.SetSubscriberAvailable("StockService", false);

        var result = Publisher.Publish(Event(EventNames.OrderCreated, 42));

        Assert.True(result.Success);
        WaitUntil(() => Sub("StockService").Failed == 1, "StockService зафиксировал ошибку");
        WaitUntil(() => Sub("Logger").Delivered == 1 && Sub("NotificationService").Delivered == 1,
            "остальные подписчики продолжили работу");
        var letter = Assert.Single(State().DeadLetters);
        Assert.Equal("StockService", letter.Subscriber);
        Assert.Equal(42, letter.EntityId);
    }

    [Fact]
    public void Publish_AfterSubscriberFailure_BrokerStillDelivers()
    {
        system.Broker.SetSubscriberAvailable("StockService", false);
        Publisher.Publish(Event(EventNames.OrderCreated));
        WaitUntil(() => Sub("StockService").Failed == 1, "первый отказ");

        var result = Publisher.Publish(Event(EventNames.PaymentCompleted, 2));

        Assert.True(result.Success);
        WaitUntil(() => Sub("NotificationService").Delivered == 2, "NotificationService получил второе событие");
    }

    [Fact]
    public void Publish_AllBrokersUnavailable_ReturnsErrorResult()
    {
        foreach (var broker in system.Brokers)
            broker.Available = false;

        var result = Publisher.Publish(Event(EventNames.OrderCreated));

        Assert.False(result.Success);
        Assert.Equal(0, result.Recipients);
        Assert.Contains("Broker недоступен", result.Message);
    }

    [Fact]
    public void NewSubscriber_ReceivesExistingEventWithoutChangingPublisher()
    {
        var publisherBefore = Publisher;

        Assert.True(system.RegisterSubscriber("AnalyticsService"));
        var result = Publisher.Publish(Event(EventNames.OrderCreated));

        Assert.Same(publisherBefore, Publisher);
        Assert.Equal(4, result.Recipients);
        WaitUntil(() => Sub("AnalyticsService").Delivered == 1, "новый подписчик получил событие");
    }

    [Fact]
    public void RedeliverDeadLetters_AfterRecovery_DeliversAgain()
    {
        system.Broker.SetSubscriberAvailable("StockService", false);
        Publisher.Publish(Event(EventNames.OrderCreated, 9));
        WaitUntil(() => State().DeadLetters.Count == 1, "событие в dead letter");

        system.Broker.SetSubscriberAvailable("StockService", true);
        Assert.Equal(1, system.Broker.RedeliverDeadLetters("StockService"));

        WaitUntil(() => Sub("StockService").Delivered == 1, "повторная доставка");
        Assert.Empty(State().DeadLetters);
    }

    [Fact]
    public void SetSubscriberAvailable_UnknownName_ReturnsFalse()
    {
        Assert.False(system.Broker.SetSubscriberAvailable("Nobody", false));
    }

    [Fact]
    public void Publish_FirstBrokerDown_FailsOverToSecondBroker()
    {
        system.Broker.Available = false;

        var result = Publisher.Publish(Event(EventNames.OrderCreated));

        Assert.True(result.Success);
        Assert.Equal("Broker-2", result.Broker);
        Assert.Equal(3, result.Recipients);
        WaitUntil(() => system.Brokers[1].GetState().Subscribers.Single(x => x.Name == "Logger").Delivered == 1,
            "Logger получил событие через Broker-2");
        Assert.Equal(0, system.Brokers[0].GetState().Subscribers.Single(x => x.Name == "Logger").Delivered);
    }

    [Fact]
    public void Publish_RecoveredBroker_IsUsedAgain()
    {
        system.Broker.Available = false;
        Publisher.Publish(Event(EventNames.OrderCancelled));
        system.Broker.Available = true;

        Assert.Equal("Broker-1", Publisher.Publish(Event(EventNames.OrderCancelled)).Broker);
    }

    [Fact]
    public void Publish_InvalidEvent_ReturnsErrorWithoutThrowing()
    {
        Assert.False(Publisher.Publish(null).Success);
        Assert.False(Publisher.Publish(Event("  ")).Success);
        Assert.False(Publisher.Publish(Event("")).Success);
    }

    [Fact]
    public void Publish_UnknownEventType_SucceedsWithNoRecipients()
    {
        var result = Publisher.Publish(Event("SomethingElse"));

        Assert.True(result.Success);
        Assert.Equal(0, result.Recipients);
        Assert.Contains("нет подписчиков", result.Message);
    }

    [Fact]
    public void Publish_SeveralSubscribersFail_OthersStillDeliver()
    {
        system.RegisterSubscriber("AnalyticsService");
        system.SetSubscriberAvailable("StockService", false);
        system.SetSubscriberAvailable("NotificationService", false);

        Publisher.Publish(Event(EventNames.OrderCreated, 5));

        WaitUntil(() => Sub("StockService").Failed == 1 && Sub("NotificationService").Failed == 1, "два отказа");
        WaitUntil(() => Sub("Logger").Delivered == 1 && Sub("AnalyticsService").Delivered == 1, "остальные доставили");
        Assert.Equal(2, State().DeadLetters.Count);
    }

    [Fact]
    public void Publish_TransientFailure_ThirdAttemptSucceeds()
    {
        system.SetSubscriberTransientFailures("NotificationService", 2);

        Publisher.Publish(Event(EventNames.OrderCreated, 8));

        WaitUntil(() => Sub("NotificationService").Delivered == 1, "успех на третьей попытке");
        Assert.Equal(0, Sub("NotificationService").Failed);
        Assert.Empty(State().DeadLetters);
        var timing = system.GetTimings().Single(t => t.Subscriber == "NotificationService");
        Assert.Equal(3, timing.Attempts);
        Assert.True(timing.Success);
    }

    [Fact]
    public void Publish_TransientFailureLongerThanAttempts_GoesToDeadLetter()
    {
        system.SetSubscriberTransientFailures("StockService", 3);

        Publisher.Publish(Event(EventNames.OrderCreated, 8));

        WaitUntil(() => Sub("StockService").Failed == 1, "три попытки провалены");
        Assert.Single(State().DeadLetters);
    }

    [Fact]
    public void Timings_RecordQueueWaitProcessingAndTotal()
    {
        Publisher.Publish(Event(EventNames.OrderCreated, 3));

        WaitUntil(() => system.GetTimings().Count(t => t.Success) == 3, "три доставки");
        Assert.All(system.GetTimings(), t =>
        {
            Assert.True(t.QueueWaitMs >= 0);
            Assert.True(t.TotalMs >= t.ProcessingMs);
        });
    }

    [Fact]
    public void Subscriber_IsRegisteredInBothBrokers()
    {
        Assert.All(system.Brokers, broker =>
            Assert.Equal(3, broker.GetState().Subscribers.Count));
        Assert.True(system.RegisterSubscriber("AnalyticsService"));
        Assert.All(system.Brokers, broker =>
            Assert.Equal(4, broker.GetState().Subscribers.Count));
    }

    [Fact]
    public void SetSubscriberTransientFailures_UnknownName_ReturnsFalse()
    {
        Assert.False(system.SetSubscriberTransientFailures("Nobody", 1));
    }
}
