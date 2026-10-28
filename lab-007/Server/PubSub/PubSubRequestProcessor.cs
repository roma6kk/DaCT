using OnlineStore.Shared;

namespace OnlineStore.Server;

public sealed class PubSubRequestProcessor(IRequestProcessor inner, PubSubSystem pubSub, Action<string> log)
    : IRequestProcessor
{
    public async Task<Response> HandleAsync(Request? request, int clientId)
    {
        switch (request?.Operation)
        {
            case "PubSubState":
                return GetState(request.Data);
            case "PubSubTimings":
                return new(true, "Замеры доставки", pubSub.GetTimings(), "PubSubTimings");
            case "SetSubscriberFlaky":
                return SetSubscriberFlaky(request.Data);
            case "SetSubscriberAvailable":
                return SetSubscriberAvailable(request.Data);
            case "SetBrokerAvailable":
                return SetBrokerAvailable(request.Data);
            case "RegisterSubscriber":
                return RegisterSubscriber(request.Data);
            case "RedeliverDeadLetters":
                return RedeliverDeadLetters(request.Data);
        }

        var response = await inner.HandleAsync(request, clientId);
        if (!response.Success || response.Event is not (EventNames.OrderCreated or EventNames.OrderCancelled or EventNames.PaymentCompleted))
            return response;

        var domainEvent = BuildEvent(response, request, clientId);
        var publish = pubSub.Publisher.Publish(domainEvent);
        if (!publish.Success)
            log($"[{Timestamp()}] Client {clientId} → PUBLISH FAIL | {publish.Message}");

        return response with { Message = $"{response.Message} | {publish.Message}" };
    }

    private static DomainEvent BuildEvent(Response response, Request? request, int clientId)
    {
        var (entityId, data) = response.Data switch
        {
            Order order => (order.Id, new Dictionary<string, string>
            {
                ["customerId"] = order.CustomerId.ToString(),
                ["total"] = order.Total.ToString()
            }),
            Payment payment => (payment.OrderId, new Dictionary<string, string> { ["amount"] = payment.Amount.ToString() }),
            _ => (int.TryParse(request?.Data?.GetValueOrDefault("orderId"), out var orderId) ? orderId : 0, null)
        };

        return new DomainEvent(response.Event!, entityId, clientId, DateTime.Now, data);
    }

    private Response GetState(Dictionary<string, string>? data)
    {
        var broker = pubSub.FindBroker(data?.GetValueOrDefault("broker"));
        return broker is null
            ? new(false, "Неизвестный Broker")
            : new(true, $"Состояние Pub/Sub ({broker.Name})", broker.GetState(), "PubSubState");
    }

    private Response SetBrokerAvailable(Dictionary<string, string>? data)
    {
        var broker = pubSub.FindBroker(data?.GetValueOrDefault("broker"));
        if (broker is null)
            return new(false, "Неизвестный Broker");

        broker.Available = data?.GetValueOrDefault("available") != "0";
        return new(true, broker.Available ? $"{broker.Name} доступен" : $"{broker.Name} недоступен (имитация)");
    }

    private Response SetSubscriberFlaky(Dictionary<string, string>? data)
    {
        var name = data?.GetValueOrDefault("subscriber") ?? "";
        if (!int.TryParse(data?.GetValueOrDefault("count"), out var count) || count < 0)
            return new(false, "Некорректное число сбоев");

        return pubSub.SetSubscriberTransientFailures(name, count)
            ? new(true, $"{name}: временный сбой на {count} обработок")
            : new(false, $"Подписчик не зарегистрирован: {name}");
    }

    private Response SetSubscriberAvailable(Dictionary<string, string>? data)
    {
        var name = data?.GetValueOrDefault("subscriber") ?? "";
        var available = data?.GetValueOrDefault("available") != "0";
        return pubSub.SetSubscriberAvailable(name, available)
            ? new(true, $"{name}: {(available ? "AVAILABLE" : "UNAVAILABLE")}")
            : new(false, $"Подписчик не зарегистрирован: {name}");
    }

    private Response RegisterSubscriber(Dictionary<string, string>? data)
    {
        var name = data?.GetValueOrDefault("subscriber") ?? "";
        return pubSub.RegisterSubscriber(name)
            ? new(true, $"Subscriber {name} зарегистрирован и подписан на все типы событий")
            : new(false, $"Не удалось зарегистрировать {name}: неизвестный или уже зарегистрированный подписчик");
    }

    private Response RedeliverDeadLetters(Dictionary<string, string>? data)
    {
        var name = data?.GetValueOrDefault("subscriber") ?? "";
        var count = pubSub.RedeliverDeadLetters(name);
        return new(true, $"Повторная доставка для {name}: событий в очереди {count}");
    }

    private static string Timestamp() => DateTime.Now.ToString("HH:mm:ss.fff");
}
