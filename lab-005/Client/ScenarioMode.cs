using OnlineStore.Shared;

namespace OnlineStore.Client;









public static class ScenarioMode
{
    public static async Task RunAsync(string[] args)
    {
        var mode = args.ElementAtOrDefault(0) ?? "race";
        var rest = args.Skip(1).ToArray();
        switch (mode)
        {
            case "race":
                await RaceAsync(true, rest);
                break;
            case "race-nolock":
                await RaceAsync(false, rest);
                break;
            case "tx":
                await TransactionAsync(rest);
                break;
            case "replication":
                await ReplicationScenarios.ReplicationAsync();
                break;
            case "failover":
                await ReplicationScenarios.FailoverAsync();
                break;
            default:
                Console.WriteLine("Использование: scenario race|race-nolock <productId> <qty> <delayMs>");
                Console.WriteLine("               scenario tx <useTx 0|1> <failAt 0|1|2> <productId> <qty>");
                Console.WriteLine("               scenario replication   (ЛР №5, Часть 2)");
                Console.WriteLine("               scenario failover      (ЛР №5, Части 3–4: отказ, Failover, Recovery)");
                break;
        }
    }

    
    public static Task RaceAsync() => RaceAsync(useLock: true, []);

    public static async Task RaceAsync(bool useLock, string[] args)
    {
        var productId = int.Parse(args.ElementAtOrDefault(0) ?? "4");
        var quantity = int.Parse(args.ElementAtOrDefault(1) ?? "1");
        var delayMs = int.Parse(args.ElementAtOrDefault(2) ?? "100");

        var client1 = new Client();
        var client2 = new Client();

        Console.WriteLine("Сброс данных эксперимента...");
        Print(await client1.SendAsync(RequestBuilder.Reset()));

        Console.WriteLine($"2 клиента одновременно бронируют товар {productId} (количество {quantity}), lock={useLock}");
        var request = RequestBuilder.CreateOrder(1, productId, quantity, useTransaction: true, useLock: useLock);

        var first = Task.Run(async () =>
        {
            var sentAt = DateTime.Now;
            var response = await client1.SendAsync(request);
            return (SentAt: sentAt, AnswerAt: DateTime.Now, Response: response);
        });
        await Task.Delay(delayMs);
        var secondSentAt = DateTime.Now;
        var secondResponse = await client2.SendAsync(request);
        var second = (SentAt: secondSentAt, AnswerAt: DateTime.Now, Response: secondResponse);

        foreach (var (name, result) in new[] { ("Client 1", await first), ("Client 2", second) })
            Console.WriteLine($"{name}: отправка {result.SentAt:HH:mm:ss.fff}, ответ {result.AnswerAt:HH:mm:ss.fff} | " +
                $"{(result.Response.Success ? "SUCCESS" : "FAIL")} — {result.Response.Message}");

        var products = await client1.SendAsync(RequestBuilder.GetProducts());
        if (products.Success && products.Data is not null)
            foreach (var product in Deserialize<List<Product>>(products.Data) ?? [])
                if (product.Id == productId)
                    Console.WriteLine($"Остаток товара {productId} после эксперимента: {product.Stock}");
    }

    private static async Task TransactionAsync(string[] args)
    {
        var useTransaction = (args.ElementAtOrDefault(0) ?? "1") != "0";
        var failAt = int.Parse(args.ElementAtOrDefault(1) ?? "0");
        var productId = int.Parse(args.ElementAtOrDefault(2) ?? "4");
        var quantity = int.Parse(args.ElementAtOrDefault(3) ?? "1");

        var client = new Client();
        Console.WriteLine("Сброс данных эксперимента...");
        Print(await client.SendAsync(RequestBuilder.Reset()));

        Console.WriteLine($"Составная операция: транзакция={useTransaction}, failAt={failAt}, " +
            $"товар {productId}, количество {quantity}");
        await PrintStateAsync(client, "Состояние БД ДО:");

        var response = await client.SendAsync(
            RequestBuilder.CreateOrder(1, productId, quantity, useTransaction, useLock: true, failAt));
        Console.WriteLine($"{(response.Success ? "OK" : "FAIL")}: {response.Message}");

        await PrintStateAsync(client, "Состояние БД ПОСЛЕ:");
    }

    
    public static async Task SendAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Использование: send <Operation> [key=value ...]");
            Console.WriteLine("Пример:        send CreateOrder customerId=1 productId=4 quantity=1 failAt=1 useTransaction=0");
            return;
        }

        var response = await new Client().SendAsync(RequestBuilder.Raw(args[0], args.Skip(1)));
        Console.WriteLine($"{(response.Success ? "OK" : "FAIL")}: {response.Message}");
        if (response.Data is not null)
            Console.WriteLine($"Data: {JsonProtocol.Serialize(response.Data)}");
    }

    private static async Task PrintStateAsync(Client client, string header)
    {
        var response = await client.SendAsync(RequestBuilder.GetState());
        if (!response.Success || response.Data is null)
        {
            Print(response);
            return;
        }

        var state = Deserialize<DatabaseState>(response.Data);
        if (state is null)
            return;

        Console.WriteLine(header);
        foreach (var product in state.Products)
            Console.WriteLine($"  products: id={product.Id} {product.Name} stock={product.Stock}");
        if (state.Orders.Count == 0)
            Console.WriteLine("  orders:   (нет записей)");
        foreach (var order in state.Orders)
            Console.WriteLine($"  orders:   id={order.Id} status={order.Status} total={order.Total}");
        if (state.Payments.Count == 0)
            Console.WriteLine("  payments: (нет записей)");
        foreach (var payment in state.Payments)
            Console.WriteLine($"  payments: id={payment.Id} order={payment.OrderId} amount={payment.Amount}");
    }

    private static T? Deserialize<T>(object data) =>
        JsonProtocol.Deserialize<T>(JsonProtocol.Serialize(data));

    private static void Print(Response response) =>
        Console.WriteLine($"{(response.Success ? "OK" : "FAIL")}: {response.Message}");

}
