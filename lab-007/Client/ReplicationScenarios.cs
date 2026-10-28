using OnlineStore.Shared;

namespace OnlineStore.Client;

public static class ReplicationScenarios
{
    public static async Task ReplicationAsync()
    {
        var client = new Client();
        Print(await client.SendAsync(RequestBuilder.Reset()));

        Console.WriteLine();
        Console.WriteLine("Таблица 2. Результаты репликации (остаток товара, stock)");
        Console.WriteLine("№ | Операция | Primary до | Primary после | Replica до | Replica после | Репликация | Результат");

        var changes = new (string Name, Func<Task<Response>> Apply, int ProductId)[]
        {
            ("Изменение 1: CreateOrder(товар 1, 1 шт.)", () => client.SendAsync(RequestBuilder.CreateOrder(1, 1, 1)), 1),
            ("Изменение 2: CreateOrder(товар 2, 2 шт.)", () => client.SendAsync(RequestBuilder.CreateOrder(1, 2, 2)), 2),
            ("Изменение 3: CancelOrder(заказ 1)", () => client.SendAsync(RequestBuilder.CancelOrder(1)), 1)
        };

        var number = 0;
        foreach (var (name, apply, productId) in changes)
        {
            number++;
            var primaryBefore = await StockAsync(client, "Primary", productId);
            var replicaBefore = await StockAsync(client, "Replica", productId);

            var response = await apply();
            Print(response);

            var primaryAfter = await StockAsync(client, "Primary", productId);
            var replicaAfter = await StockAsync(client, "Replica", productId);
            var compare = await CompareAsync(client);

            Console.WriteLine($"{number} | {name} | {primaryBefore} | {primaryAfter} | {replicaBefore} | {replicaAfter} | " +
                              $"{(compare?.Consistent == true ? "SUCCESS" : "FAIL")} | {(response.Success ? "OK" : "FAIL")}");
        }
    }

    public static async Task FailoverAsync()
    {
        var client1 = new Client();
        var client2 = new Client();

        Print(await client1.SendAsync(RequestBuilder.Reset()));

        Console.WriteLine();
        Console.WriteLine("Шаг 1. Штатное состояние:");
        Console.WriteLine($"  {await StatusAsync(client1)}");

        Console.WriteLine();
        Console.WriteLine("Шаг 2. Имитация отказа Primary (программное отключение экземпляра):");
        Print(await client1.SendAsync(RequestBuilder.SimulateFailure("Primary", enabled: true)));
        Console.WriteLine($"  {await StatusAsync(client1)}");

        Console.WriteLine();
        Console.WriteLine("Таблица 3. Результаты отказа");
        Console.WriteLine("№ | Клиент | Операция | Primary | Replica | Результат | Использованный экземпляр");

        await LogRowAsync(1, "Client 1", "Read", client1, () => client1.SendAsync(RequestBuilder.GetProducts()));
        await LogRowAsync(2, "Client 2", "Read", client2, () => client2.SendAsync(RequestBuilder.GetProducts()));
        await LogRowAsync(3, "Client 1", "Update", client1, () => client1.SendAsync(RequestBuilder.CreateOrder(1, 3, 1)));
        await LogRowAsync(4, "Client 2", "Read", client2, () => client2.SendAsync(RequestBuilder.GetProducts()));

        Console.WriteLine();
        Console.WriteLine("Состояние копий во время отказа (Primary недоступен, данные изменены только в Replica):");
        var during = await CompareAsync(client1);
        if (during is not null)
            Console.WriteLine($"  расхождений: {during.Differences.Count}");

        Console.WriteLine();
        Console.WriteLine("Шаг 7. Восстановление Primary (сравнение и синхронизация Replica → Primary):");
        var recovery = await client1.SendAsync(RequestBuilder.RecoverPrimary());
        Print(recovery);
        var report = Deserialize<RecoveryReport>(recovery.Data);
        if (report is not null)
        {
            Console.WriteLine($"  расхождений до синхронизации: {report.DifferencesBefore.Count}");
            foreach (var difference in report.DifferencesBefore)
                Console.WriteLine($"    - {difference}");
            Console.WriteLine($"  расхождений после синхронизации: {report.DifferencesAfter.Count}");
        }
        Console.WriteLine($"  {await StatusAsync(client1)}");

        Console.WriteLine();
        Console.WriteLine("Шаг 9. Контрольная операция после восстановления и проверка Primary = Replica:");
        Print(await client1.SendAsync(RequestBuilder.CreateOrder(1, 4, 1)));
        var final = await CompareAsync(client1);
        if (final is not null)
            Console.WriteLine($"  {(final.Consistent ? "Primary = Replica: копии совпадают" : "ОШИБКА: расхождения " + final.Differences.Count)}");
    }

    private static async Task LogRowAsync(int number, string clientName, string operation, Client statusClient,
        Func<Task<Response>> request)
    {
        var info = await InstancesAsync(statusClient);
        var response = await request();
        Console.WriteLine($"{number} | {clientName} | {operation} | {info?.Primary} | {info?.Replica} | " +
                          $"{(response.Success ? "SUCCESS" : "FAIL")} ({response.Message}) | {response.Instance ?? "—"}");
    }

    private static async Task<InstancesInfo?> InstancesAsync(Client client)
    {
        var response = await client.SendAsync(RequestBuilder.GetInstancesState());
        return Deserialize<InstancesInfo>(response.Data);
    }

    private static async Task<string> StatusAsync(Client client)
    {
        var info = await InstancesAsync(client);
        return info is null ? "состояние недоступно" : $"Primary → {info.Primary}, Replica → {info.Replica}";
    }

    private static async Task<int?> StockAsync(Client client, string instance, int productId)
    {
        var response = await client.SendAsync(RequestBuilder.GetProductsFrom(instance));
        var products = Deserialize<List<Product>>(response.Data);
        return products?.FirstOrDefault(product => product.Id == productId)?.Stock;
    }

    
    private static async Task<ConsistencyReport?> CompareAsync(Client client)
    {
        var response = await client.SendAsync(RequestBuilder.Compare());
        if (!response.Success)
            Console.WriteLine($"  Сравнение невозможно: {response.Message}");
        return Deserialize<ConsistencyReport>(response.Data);
    }

    private static T? Deserialize<T>(object? data) =>
        data is null ? default : JsonProtocol.Deserialize<T>(JsonProtocol.Serialize(data));

    private static void Print(Response response) =>
        Console.WriteLine($"{(response.Success ? "OK" : "FAIL")}: {response.Message}");
}
