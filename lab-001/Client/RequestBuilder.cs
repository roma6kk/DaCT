using OnlineStore.Shared;

namespace OnlineStore.Client;

public static class RequestBuilder
{
    public static Request GetProducts() => new("GetProducts");

    public static Request CreateOrder(int customerId, int productId, int quantity) =>
        new("CreateOrder", new Dictionary<string, string>
        {
            ["customerId"] = customerId.ToString(),
            ["productId"] = productId.ToString(),
            ["quantity"] = quantity.ToString()
        });

    public static Request CancelOrder(int orderId) => new("CancelOrder", new Dictionary<string, string>
    {
        ["orderId"] = orderId.ToString()
    });

    public static Request PayOrder(int orderId) => new("PayOrder", new Dictionary<string, string>
    {
        ["orderId"] = orderId.ToString()
    });
}
