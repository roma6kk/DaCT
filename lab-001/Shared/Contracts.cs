using System.Text.Json;
using System.Text.Encodings.Web;

namespace OnlineStore.Shared;

public sealed record Request(string Operation, Dictionary<string, string>? Data = null);

public sealed record Response(bool Success, string Message, object? Data = null, string? Event = null);

public sealed record Product(int Id, string Name, decimal Price, int Stock);

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

public static class JsonProtocol
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, Options);
}
