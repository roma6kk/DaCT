using OnlineStore.Client;
using OnlineStore.Shared;

namespace OnlineStore.Client;

public static class ScenarioMode
{
    public static async Task RunAsync(string[] args)
    {
        var offsetSeconds = double.Parse(args.ElementAtOrDefault(0) ?? "0");
        var productId = int.Parse(args.ElementAtOrDefault(1) ?? "4");
        var quantity = int.Parse(args.ElementAtOrDefault(2) ?? "1");
        var delayBetween = int.Parse(args.ElementAtOrDefault(3) ?? "1000");

        var client1 = new Client(clockOffset: TimeSpan.FromSeconds(offsetSeconds));
        var client2 = new Client();
        var orderRequest = RequestBuilder.CreateOrder(1, productId, quantity);

        var first = Task.Run(async () =>
        {
            await Task.Delay(delayBetween / 2);
            return await client1.SendAsync(orderRequest);
        });
        await Task.Delay(delayBetween);
        var second = client2.SendAsync(orderRequest);

        var responses = await Task.WhenAll(first, second);
        for (var i = 0; i < responses.Length; i++)
            Console.WriteLine(
                $"Client {i + 1}: {(responses[i].Success ? "SUCCESS" : "FAIL")} — {responses[i].Message}" +
                $" | OrderId = {(responses[i].Data as Order)?.Id}");

        var sync = await client1.SyncTimeAsync();
        Console.WriteLine($"ClientLocalTime = {Client.FormatTime(sync.ClientLocalTime)}");
        Console.WriteLine($"ServerTime      = {Client.FormatTime(sync.ServerTime!.Value)}");
        Console.WriteLine($"RTT             = {sync.Rtt!.Value.TotalMilliseconds:F1} мс");
        Console.WriteLine($"RTT/2 (delay)   = {sync.EstimatedDelay!.Value.TotalMilliseconds:F1} мс");
        Console.WriteLine($"Offset          = {sync.Offset!.Value.TotalMilliseconds:F1} мс");
        Console.WriteLine($"CorrectedTime   = {Client.FormatTime(client1.CorrectedTime)}");

        var products = await client1.SendAsync(RequestBuilder.GetProducts());
        Console.WriteLine("Stock после заказов:");
        foreach (var product in System.Text.Json.JsonSerializer.Deserialize<List<Product>>(products.Data!.ToString()!) ?? [])
            Console.WriteLine($"  {product.Id}. {product.Name}: {product.Stock}");
    }
}