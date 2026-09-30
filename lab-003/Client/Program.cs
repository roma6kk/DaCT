using OnlineStore.Client;
using OnlineStore.Shared;

if (args.Length > 0 && args[0] == "scenario")
{
    await ScenarioMode.RunAsync(args.Skip(1).ToArray());
    return;
}
// отличие симафора и слима
// объект лок
TimeSpan clockOffset = TimeSpan.Zero;
var argList = args.ToList();
var offsetIndex = argList.IndexOf("--offset");
if (offsetIndex >= 0 && offsetIndex + 1 < argList.Count && double.TryParse(argList[offsetIndex + 1], out var offsetSeconds))
    clockOffset = TimeSpan.FromSeconds(offsetSeconds);

var client = new Client(clockOffset: clockOffset);

Console.WriteLine("=== Интернет-магазин (ЛР №3) ===");
if (clockOffset != TimeSpan.Zero)
    Console.WriteLine($"Смещение часов клиента: {clockOffset.TotalSeconds:+0;-0} с");

while (true)
{
    Console.WriteLine();
    Console.WriteLine("1 - Показать товары");
    Console.WriteLine("2 - Создать заказ");
    Console.WriteLine("3 - Отменить заказ");
    Console.WriteLine("4 - Оплатить заказ");
    Console.WriteLine("5 - Синхронизировать время (TIME_REQUEST)");
    Console.WriteLine("0 - Выход");
    Console.Write("Выберите действие: ");
    var choice = Console.ReadLine();

    try
    {
        switch (choice)
        {
            case "1":
                await ShowProductsAsync();
                break;
            case "2":
                await CreateOrderAsync();
                break;
            case "3":
                await CancelOrderAsync();
                break;
            case "4":
                await PayOrderAsync();
                break;
            case "5":
                await SyncTimeAsync();
                break;
            case "0":
                return;
        }
    }
    catch (Exception exception)
    {
        Console.WriteLine($"Ошибка: {exception.Message}");
    }
}

async Task ShowProductsAsync()
{
    var response = await client.SendAsync(RequestBuilder.GetProducts());
    Print(response);
    if (response.Success && response.Data is not null)
        foreach (var product in System.Text.Json.JsonSerializer.Deserialize<List<OnlineStore.Shared.Product>>(response.Data.ToString()!) ?? [])
            Console.WriteLine($"{product.Id}. {product.Name} — {product.Price} ₽, на складе: {product.Stock}");
}

async Task CreateOrderAsync()
{
    Console.Write("ID покупателя: ");
    var customerId = int.Parse(Console.ReadLine() ?? "1");
    Console.Write("ID товара: ");
    var productId = int.Parse(Console.ReadLine() ?? "1");
    Console.Write("Количество: ");
    var quantity = int.Parse(Console.ReadLine() ?? "1");

    var response = await client.SendAsync(RequestBuilder.CreateOrder(customerId, productId, quantity));
    Print(response);
}

async Task CancelOrderAsync()
{
    Console.Write("ID заказа: ");
    var orderId = int.Parse(Console.ReadLine() ?? "1");
    var response = await client.SendAsync(RequestBuilder.CancelOrder(orderId));
    Print(response);
}

async Task PayOrderAsync()
{
    Console.Write("ID заказа: ");
    var orderId = int.Parse(Console.ReadLine() ?? "1");
    var response = await client.SendAsync(RequestBuilder.PayOrder(orderId));
    Print(response);
}

async Task SyncTimeAsync()
{
    var result = await client.SyncTimeAsync();
    if (!result.Success)
    {
        Console.WriteLine($"Синхронизация не удалась: {result.Error}");
        return;
    }

    Console.WriteLine($"Локальное время клиента:        {Client.FormatTime(result.ClientLocalTime)}");
    Console.WriteLine($"Время сервера (ServerTime):     {Client.FormatTime(result.ServerTime!.Value)}");
    Console.WriteLine($"RTT:                            {result.Rtt!.Value.TotalMilliseconds:F1} мс");
    Console.WriteLine($"Оценка сетевой задержки (RTT/2): {result.EstimatedDelay!.Value.TotalMilliseconds:F1} мс");
    Console.WriteLine($"Поправка (Offset):              {result.Offset!.Value.TotalMilliseconds:+0.0;-0.0} мс");
    Console.WriteLine($"Скорректированное время:        {Client.FormatTime(client.CorrectedTime)}");
}

static void Print(OnlineStore.Shared.Response response) =>
    Console.WriteLine($"{(response.Success ? "OK" : "FAIL")}: {response.Message}");