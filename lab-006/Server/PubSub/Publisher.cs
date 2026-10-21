using OnlineStore.Shared;

namespace OnlineStore.Server;

public sealed record PublishResult(bool Success, string Message, int Recipients, string? Broker = null);
public sealed class Publisher(IReadOnlyList<Broker> brokers, Action<string>? log = null)
{
    private readonly Action<string> log = log ?? (_ => { });

    public Publisher(Broker broker, Action<string>? log = null) : this([broker], log)
    {
    }

    public PublishResult Publish(DomainEvent? domainEvent)
    {
        if (domainEvent is null || string.IsNullOrWhiteSpace(domainEvent.EventType))
        {
            log($"[{Timestamp()}] Publisher → FAIL | некорректное событие");
            return new(false, "Событие не опубликовано: некорректное событие (пустой тип)", 0);
        }

        log($"[{Timestamp()}] Client {domainEvent.ClientId} → Publisher → PUBLISH | {domainEvent.EventType} #{domainEvent.EntityId}");
        foreach (var broker in brokers)
        {
            try
            {
                var recipients = broker.Accept(domainEvent);
                var note = recipients == 0 ? " (нет подписчиков на этот тип)" : "";
                return new(true,
                    $"Событие {domainEvent.EventType} опубликовано через {broker.Name}, получателей: {recipients}{note}",
                    recipients, broker.Name);
            }
            catch (BrokerUnavailableException exception)
            {
                log($"[{Timestamp()}] Client {domainEvent.ClientId} → Publisher → {broker.Name} FAIL | {exception.Message}");
            }
        }

        return new(false, $"Событие {domainEvent.EventType} не опубликовано: Broker недоступен (проверено Broker: {brokers.Count})", 0);
    }

    private static string Timestamp() => DateTime.Now.ToString("HH:mm:ss.fff");
}
