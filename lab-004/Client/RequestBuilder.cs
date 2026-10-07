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

    public static Request TimeRequest() => new("TIME_REQUEST");

    
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
