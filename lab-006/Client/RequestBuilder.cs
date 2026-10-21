using OnlineStore.Shared;

namespace OnlineStore.Client;

public static class RequestBuilder
{
    public static Request GetProducts() => new("GetProducts");

    
    public static Request GetState() => new("GetState");

    
    public static Request Reset() => new("Reset");

    
    
    
    public static Request CreateOrder(int customerId, int productId, int quantity,
        bool useTransaction = true, bool useLock = true, int failAt = 0) =>
        new("CreateOrder", new Dictionary<string, string>
        {
            ["customerId"] = customerId.ToString(),
            ["productId"] = productId.ToString(),
            ["quantity"] = quantity.ToString(),
            ["useTransaction"] = useTransaction ? "1" : "0",
            ["useLock"] = useLock ? "1" : "0",
            ["failAt"] = failAt.ToString()
        });

    public static Request CancelOrder(int orderId) => new("CancelOrder", new Dictionary<string, string>
    {
        ["orderId"] = orderId.ToString()
    });

    public static Request PayOrder(int orderId) => new("PayOrder", new Dictionary<string, string>
    {
        ["orderId"] = orderId.ToString()
    });

    public static Request GetInstancesState() => new("GetInstancesState");

    
    public static Request GetProductsFrom(string instance) => new("GetProducts", new Dictionary<string, string>
    {
        ["instance"] = instance
    });

    public static Request SimulateFailure(string instance, bool enabled) => new("SimulateFailure", new Dictionary<string, string>
    {
        ["instance"] = instance,
        ["enabled"] = enabled ? "1" : "0"
    });

    public static Request Compare() => new("Compare");

    public static Request RecoverPrimary() => new("RecoverPrimary");

    public static Request TimeRequest() => new("TIME_REQUEST");

    public static Request PubSubState(string? broker = null) => new("PubSubState",
        broker is null ? null : new Dictionary<string, string> { ["broker"] = broker });

    public static Request PubSubTimings() => new("PubSubTimings");

    public static Request SetSubscriberFlaky(string subscriber, int count) => new("SetSubscriberFlaky", new Dictionary<string, string>
    {
        ["subscriber"] = subscriber,
        ["count"] = count.ToString()
    });

    public static Request SetSubscriberAvailable(string subscriber, bool available) => new("SetSubscriberAvailable", new Dictionary<string, string>
    {
        ["subscriber"] = subscriber,
        ["available"] = available ? "1" : "0"
    });

    public static Request SetBrokerAvailable(bool available, string broker = "Broker-1") => new("SetBrokerAvailable", new Dictionary<string, string>
    {
        ["broker"] = broker,
        ["available"] = available ? "1" : "0"
    });

    public static Request RegisterSubscriber(string subscriber) => new("RegisterSubscriber", new Dictionary<string, string>
    {
        ["subscriber"] = subscriber
    });

    public static Request RedeliverDeadLetters(string subscriber) => new("RedeliverDeadLetters", new Dictionary<string, string>
    {
        ["subscriber"] = subscriber
    });

    
    public static Request Raw(string operation, IEnumerable<string> arguments)
    {
        var data = new Dictionary<string, string>();
        foreach (var argument in arguments)
        {
            var separator = argument.IndexOf('=');
            if (separator > 0)
                data[argument[..separator]] = argument[(separator + 1)..];
        }
        return new Request(operation, data.Count > 0 ? data : null);
    }
}
