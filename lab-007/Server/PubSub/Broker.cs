using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using OnlineStore.Shared;

namespace OnlineStore.Server;


public sealed class Broker : IDisposable
{
    private readonly ConcurrentDictionary<string, Registration> registrations = new();
    private readonly ConcurrentQueue<DeliveryTiming> timings = new();
    private readonly List<DeadLetter> deadLetters = [];
    private readonly object deadLettersLock = new();
    private readonly Action<string> log;
    private readonly int maxAttempts;
    private readonly TimeSpan retryDelay;
    private volatile bool available = true;

    public Broker(Action<string>? log = null, int maxAttempts = 3, TimeSpan? retryDelay = null, string name = "Broker-1")
    {
        this.log = log ?? (_ => { });
        this.maxAttempts = Math.Max(1, maxAttempts);
        this.retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(300);
        Name = name;
    }

    public string Name { get; }

    public bool Available
    {
        get => available;
        set => available = value;
    }

    public bool Subscribe(Subscriber subscriber)
    {
        var channel = Channel.CreateUnbounded<Envelope>();
        var registration = new Registration(subscriber, channel);
        if (!registrations.TryAdd(subscriber.Name, registration))
            return false;

        _ = Task.Run(() => RunWorkerAsync(registration));
        log($"[{Timestamp()}] {Name} → SUBSCRIBE | {subscriber.Name} → {string.Join(", ", subscriber.EventTypes)}");
        return true;
    }

    public int Accept(DomainEvent domainEvent)
    {
        if (!Available)
            throw new BrokerUnavailableException($"{Name} недоступен");

        log($"[{Timestamp()}] Client {domainEvent.ClientId} → {Name} → EVENT RECEIVED | {domainEvent.EventType} #{domainEvent.EntityId}");

        var recipients = registrations.Values
            .Where(registration => registration.Subscriber.EventTypes.Contains(domainEvent.EventType))
            .ToList();

        if (recipients.Count == 0)
            log($"[{Timestamp()}] {Name} → нет подписчиков на {domainEvent.EventType}");

        var envelope = new Envelope(domainEvent, Stopwatch.GetTimestamp());
        foreach (var registration in recipients)
        {
            registration.Channel.Writer.TryWrite(envelope);
            log($"[{Timestamp()}] {Name} → {registration.Subscriber.Name} | QUEUED");
        }

        return recipients.Count;
    }

    public bool SetSubscriberAvailable(string name, bool value)
    {
        if (!registrations.TryGetValue(name, out var registration))
            return false;

        registration.Subscriber.Available = value;
        log($"[{Timestamp()}] {name} → {(value ? "AVAILABLE" : "UNAVAILABLE")} | имитация");
        return true;
    }

    public bool SetSubscriberTransientFailures(string name, int count)
    {
        if (!registrations.TryGetValue(name, out var registration))
            return false;

        registration.Subscriber.FailNext(count);
        log($"[{Timestamp()}] {name} → временный сбой на {count} обработок | имитация");
        return true;
    }

    public int RedeliverDeadLetters(string subscriberName)
    {
        if (!registrations.TryGetValue(subscriberName, out var registration))
            return 0;

        List<DeadLetter> toRetry;
        lock (deadLettersLock)
        {
            toRetry = deadLetters.Where(letter => letter.Subscriber == subscriberName).ToList();
            deadLetters.RemoveAll(letter => letter.Subscriber == subscriberName);
        }

        foreach (var letter in toRetry)
            registration.Channel.Writer.TryWrite(new Envelope(letter.Event, Stopwatch.GetTimestamp()));

        log($"[{Timestamp()}] {Name} → REDELIVER | {subscriberName}: {toRetry.Count} событий");
        return toRetry.Count;
    }

    public PubSubState GetState()
    {
        var subscribers = registrations.Values
            .OrderBy(registration => registration.Subscriber.Name)
            .Select(registration => new SubscriberInfo(
                registration.Subscriber.Name,
                registration.Subscriber.EventTypes.ToList(),
                registration.Subscriber.Available,
                registration.Delivered,
                registration.Failed))
            .ToList();

        List<DeadLetterInfo> letters;
        lock (deadLettersLock)
        {
            letters = deadLetters
                .Select(letter => new DeadLetterInfo(letter.Subscriber, letter.Event.EventType, letter.Event.EntityId, letter.Error))
                .ToList();
        }

        return new PubSubState(Available, subscribers, letters, Name);
    }

    public IReadOnlyList<DeliveryTiming> GetTimings() => timings.ToArray();

    public void Dispose()
    {
        foreach (var registration in registrations.Values)
            registration.Channel.Writer.TryComplete();
    }

    private async Task RunWorkerAsync(Registration registration)
    {
        await foreach (var envelope in registration.Channel.Reader.ReadAllAsync())
            await DeliverAsync(registration, envelope);
    }

    private async Task DeliverAsync(Registration registration, Envelope envelope)
    {
        var domainEvent = envelope.Event;
        var name = registration.Subscriber.Name;
        var startedAt = Stopwatch.GetTimestamp();
        string error = "";

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var result = await registration.Subscriber.HandleAsync(domainEvent);
                Interlocked.Increment(ref registration.delivered);
                log($"[{Timestamp()}] {name} → SUCCESS | {domainEvent.EventType} #{domainEvent.EntityId}, попытка {attempt}: {result}");
                RecordTiming(name, envelope, startedAt, true, attempt);
                return;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                log($"[{Timestamp()}] {name} → Attempt {attempt} → FAIL | {exception.Message}");
                if (attempt < maxAttempts)
                    await Task.Delay(retryDelay);
            }
        }

        Interlocked.Increment(ref registration.failed);
        lock (deadLettersLock)
            deadLetters.Add(new DeadLetter(name, domainEvent, error));
        log($"[{Timestamp()}] {Name} → {name} → DEAD LETTER | {domainEvent.EventType} #{domainEvent.EntityId}: {error}");
        RecordTiming(name, envelope, startedAt, false, maxAttempts);
    }

    private void RecordTiming(string subscriber, Envelope envelope, long startedAt, bool success, int attempts)
    {
        var finishedAt = Stopwatch.GetTimestamp();
        timings.Enqueue(new DeliveryTiming(
            Name, subscriber, envelope.Event.EventType, envelope.Event.EntityId,
            Stopwatch.GetElapsedTime(envelope.AcceptedAt, startedAt).TotalMilliseconds,
            Stopwatch.GetElapsedTime(startedAt, finishedAt).TotalMilliseconds,
            Stopwatch.GetElapsedTime(envelope.AcceptedAt, finishedAt).TotalMilliseconds,
            success, attempts, envelope.Event.Timestamp));
    }

    private static string Timestamp() => DateTime.Now.ToString("HH:mm:ss.fff");

    private sealed record Envelope(DomainEvent Event, long AcceptedAt);

    private sealed class Registration(Subscriber subscriber, Channel<Envelope> channel)
    {
        public Subscriber Subscriber { get; } = subscriber;

        public Channel<Envelope> Channel { get; } = channel;

        public int delivered;

        public int failed;

        public int Delivered => Volatile.Read(ref delivered);

        public int Failed => Volatile.Read(ref failed);
    }

    private sealed record DeadLetter(string Subscriber, DomainEvent Event, string Error);
}
