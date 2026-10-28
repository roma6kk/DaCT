using System.Text.Json;
using System.Text.Encodings.Web;

namespace OnlineStore.Shared;

public sealed record Request(string Operation, Dictionary<string, string>? Data = null);

public sealed record Response(bool Success, string Message, object? Data = null, string? Event = null, string? Instance = null);

public sealed record Product(int Id, string Name, decimal Price)
{
    public int Stock { get; set; }
}

public sealed class Customer
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
}

public sealed class OrderItem
{
    public int ProductId { get; init; }
    public int Quantity { get; init; }
    public decimal Price { get; init; }
}

public sealed class Order
{
    public int Id { get; init; }
    public int CustomerId { get; init; }
    public List<OrderItem> Items { get; init; } = [];
    public decimal Total { get; init; }
    public string Status { get; set; } = "Created";
}

public sealed class Payment
{
    public int Id { get; init; }
    public int OrderId { get; init; }
    public decimal Amount { get; init; }
    public string Status { get; init; } = "Completed";
    public DateTime CreatedAt { get; init; }
}


public sealed record DatabaseState(List<Product> Products, List<Order> Orders, List<Payment> Payments);


public sealed record TimeInfo(DateTime ServerTimeUtc);

public static class InstanceStatus
{
    public const string Available = "AVAILABLE";
    public const string Unavailable = "UNAVAILABLE";
}


public sealed record InstancesInfo(string Primary, string Replica, bool ReplicaInSync);


public sealed record ConsistencyReport(List<string> Differences, bool Consistent);


public sealed record RecoveryReport(List<string> DifferencesBefore, List<string> DifferencesAfter, bool Consistent);





public interface IRequestProcessor
{
    
    
    Task<Response> HandleAsync(Request? request, int clientId);
}

public static class JsonProtocol
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, Options);
}