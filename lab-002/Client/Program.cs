using OnlineStore.Client;

var client = new Client();
while (true)
{
	Console.WriteLine("1 - товары");
	Console.WriteLine("2 - создать заказ");
	Console.WriteLine("3 - отменить заказ");
	Console.WriteLine("4 - оплатить заказ");
	Console.WriteLine("0 - выход");
	Console.Write("Операция: ");

	var operation = Console.ReadLine();
	if (operation == "0")
		break;

	if (operation == "2")
	{
		var productsResponse = await client.SendAsync(RequestBuilder.GetProducts());
		PrintResponse(productsResponse);
		var productId = ReadPositiveInt("ID товара: ");
		var quantity = ReadPositiveInt("Количество: ");
		PrintResponse(await client.SendAsync(RequestBuilder.CreateOrder(1, productId, quantity)));
		continue;
	}

	if (operation is "3" or "4")
	{
		var orderId = ReadPositiveInt("ID заказа: ");
		var response = operation == "3"
			? await client.SendAsync(RequestBuilder.CancelOrder(orderId))
			: await client.SendAsync(RequestBuilder.PayOrder(orderId));
		PrintResponse(response);
		continue;
	}

	var result = operation == "1"
		? await client.SendAsync(RequestBuilder.GetProducts())
		: new OnlineStore.Shared.Response(false, "Некорректная операция");
	PrintResponse(result);
}

static int ReadPositiveInt(string prompt)
{
	while (true)
	{
		Console.Write(prompt);
		if (int.TryParse(Console.ReadLine(), out var value) && value > 0)
			return value;
		Console.WriteLine("Введите положительное целое число.");
	}
}

static void PrintResponse(OnlineStore.Shared.Response response)
{
	Console.WriteLine(response.Message);
	Console.WriteLine(OnlineStore.Shared.JsonProtocol.Serialize(response.Data));
}

