## 2. Запуск

```bash
# сервер (порт 5000)
dotnet run --project Server/OnlineStore.Server.csproj

# клиент №1 (в отдельном терминале)
dotnet run --project Client/OnlineStore.Client.csproj

# клиент №2 (в ещё одном терминале)
dotnet run --project Client/OnlineStore.Client.csproj

# все тесты
dotnet test
```

Меню клиента: `1` — товары, `2` — создать заказ, `3` — отменить заказ, `4` — оплатить заказ,
`0` — выход.

## 3. Требование 1 и 2. Приём клиентов и обработка каждого клиента в отдельной Task

`Server/Server.cs`:

```csharp
public sealed class Server(int port = 5000, BusinessLogic? businessLogic = null, Action<string>? log = null)
{
    private readonly BusinessLogic businessLogic = businessLogic ?? new();
    private readonly Action<string> log = log ?? Console.WriteLine;
    private int clientCounter;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        log($"Сервер запущен: 127.0.0.1:{port}");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                var clientId = Interlocked.Increment(ref clientCounter);
                _ = Task.Run(() => new RequestHandler(businessLogic, clientId, log).HandleAsync(client));
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            listener.Stop();
        }
    }
}
```

Что здесь важно:

* `while (!cancellationToken.IsCancellationRequested)` + `await AcceptTcpClientAsync(...)` — цикл приёма
  **продолжается сразу** после выдачи задания на обработку, потому что `HandleAsync` не ожидается (`_ =`).
  Если бы стояло `await new RequestHandler(...).HandleAsync(client);`, сервер обслуживал бы клиентов
  **последовательно** (второй ждал бы 10 секунд окончания первого).
* `Task.Run(() => handler.HandleAsync(client))` — перегрузка `Task.Run(Func<Task>)`: лямбда возвращает
  `Task`, поэтому раннер возвращает «развёрнутый» (`Unwrap`) `Task`, а работа выполняется в потоке из
  **Thread Pool**. Лямбда захватывает `client`, `clientId`, `log` (замыкание).
* `_ = ...` — намеренный «fire-and-forget»: сервер не хранит ссылку на задачу клиента. Это допустимо,
  потому что `RequestHandler.HandleAsync` внутри сам ловит все исключения и не выпускает их наружу.
* `Interlocked.Increment(ref clientCounter)` — атомарный инкремент, **возвращает новое значение** (1, 2, 3…).
  Обычный `clientCounter++` при нескольких одновременно принимаемых клиентах мог бы выдать одинаковые номера.
* Параметры конструктора `Server` со значениями по умолчанию (`port = 5000`, `businessLogic = null`,
  `log = null`) — обычный способ внедрения зависимостей: в проде `log` равен `Console.WriteLine`
  (через `??`), а тесты подставляют свою бизнес-логику и свой сборщик логов.

## 4. Требование 3. Идентификаторы клиентов и временные метки

`Server/RequestHandler.cs`:

```csharp
public sealed class RequestHandler(BusinessLogic businessLogic, int clientId, Action<string> log)
{
    public async Task HandleAsync(TcpClient client)
    {
        using var connection = client;
        using var stream = connection.GetStream();
        using var reader = new StreamReader(stream);
        using var writer = new StreamWriter(stream) { AutoFlush = true };

        var request = await ReadRequestAsync(reader);
        var operation = request?.Operation ?? "Unknown";
        var taskId = Task.CurrentId;
        var threadId = Environment.CurrentManagedThreadId;
        log($"[{Timestamp()}] Client {clientId} START | Operation: {operation} | Task: {taskId} | Thread: {threadId}");

        var stopwatch = Stopwatch.StartNew();
        Response response;
        try
        {
            response = await businessLogic.HandleAsync(request);
        }
        catch (Exception exception)
        {
            response = new(false, "Ошибка обработки запроса");
            log($"[{Timestamp()}] Client {clientId} ERROR | {exception.GetType().Name}: {exception.Message}");
        }
        stopwatch.Stop();

        log($"[{Timestamp()}] Client {clientId} END | Operation: {operation} | Task: {taskId} | Thread: {threadId} | Elapsed: {stopwatch.Elapsed.TotalSeconds:F1} s");

        await SendResponseAsync(writer, response);
    }

    private static string Timestamp() => DateTime.Now.ToString("HH:mm:ss.fff");

    private static async Task<Request?> ReadRequestAsync(StreamReader reader)
    {
        try
        {
            return JsonProtocol.Deserialize<Request>(await reader.ReadLineAsync() ?? "");
        }
        catch
        {
            return null;
        }
    }

    private async Task SendResponseAsync(StreamWriter writer, Response response)
    {
        try
        {
            await writer.WriteLineAsync(JsonProtocol.Serialize(response));
        }
        catch (Exception exception)
        {
            log($"[{Timestamp()}] Client {clientId} ОТКЛЮЧЁН | {exception.GetType().Name}: {exception.Message}");
        }
    }
}
```

Что здесь важно:

* **Идентификатор клиента** приходит в конструкторе (`int clientId`) и печатается как `Client 1`,
  `Client 2`, … — по нему видно, кто именно обрабатывается.
* Все обязательные данные Требования 3 печатаются в одном формате: время (`HH:mm:ss.fff`), номер клиента,
  `START`/`END`, название операции (`Operation: CreateOrder`), `Task:` — идентификатор задачи,
  `Thread:` — идентификатор управляемого потока, `Elapsed` — длительность обработки.
* `Task.CurrentId` возвращает идентификатор **текущей выполняемой задачи**, но вне тела задачи и после
  `await` он равен `null` (продолжение асинхронного метода выполняется как continuation, а не «внутри» той
  же задачи). Поэтому значение читается **один раз в начале** в переменную `taskId` и печатается и в
  `START`, и в `END` — иначе поле `Task` в строке `END` было бы пустым.
* `Environment.CurrentManagedThreadId` — идентификатор управляемого потока. В реальных логах видно, что
  один и тот же метод после `await` вполне может продолжиться в другом потоке, а задачи `Task 1` и `Task 2`
  работали в потоках `7` и `5`.
* `$"..."` — интерполяция строк; `:HH:mm:ss.fff` — формат даты; `{...:F1}` — один знак после запятой.
* `ReadRequestAsync` — «мягкий» разбор запроса: любое исключение JSON превращается в `null`, который
  бизнес-логика трактует как `Некорректный запрос`.
* `SendResponseAsync` — отключение клиента во время обработки не роняет задачу: исключение отправки
  логируется как `ОТКЛЮЧЁН`, а сервер продолжает работать.
* `using var` — детерминированное освобождение сокета/потоков при выходе из метода (даже при исключении),
  поэтому отключившийся клиент не оставляет за собой открытые соединения.

Пример консольного вывода сервера (реальный лог эксперимента):

```
Сервер запущен: 127.0.0.1:5000
[18:16:38.163] Client 1 START | Operation: CreateOrder | Task: 1 | Thread: 7
[18:16:39.129] Client 2 START | Operation: CreateOrder | Task: 2 | Thread: 5
[18:16:48.178] Client 1 END | Operation: CreateOrder | Task: 1 | Thread: 7 | Elapsed: 10,0 s
[18:16:49.130] Client 2 END | Operation: CreateOrder | Task: 2 | Thread: 5 | Elapsed: 10,0 s
```

## 5. Требование 4 и 5. Искусственная задержка и эксперимент «параллельная обработка»

`Server/BusinessLogic.cs` — задержка и «опасный участок» (проверка, затем пауза, затем изменение склада):

```csharp
public sealed class BusinessLogic
{
    private static readonly TimeSpan DefaultProcessingDelay = TimeSpan.FromSeconds(10);
    private readonly TimeSpan processingDelay;

    public BusinessLogic(TimeSpan? processingDelay = null)
    {
        this.processingDelay = processingDelay ?? DefaultProcessingDelay;
    }

    private async Task<Response> CreateOrderAsync(Dictionary<string, string>? data)
    {
        // ... проверки входных данных, покупателя и товара ...

        if (product.Stock < quantity)
            return new(false, "Недостаточно товара на складе");

        await Task.Delay(processingDelay);          // Требование 4: искусственная задержка 10 с

        var order = new Order { /* ... */ };

        product.Stock -= quantity;                  // Требование 6: изменение ресурса ПОСЛЕ паузы
        orders.Add(order);
        return new(true, "Заказ создан", order, "OrderCreated");
    }
}
```

Что здесь важно:

* `TimeSpan? processingDelay = null` — необязательный параметр-«количество времени»; `TimeSpan?` — это
  `Nullable<TimeSpan>`, значение по умолчанию `null`. Строка `processingDelay ?? DefaultProcessingDelay`
  даёт **ровно 10 секунд**, если параметр не передан: требование «добавьте задержку в 10 секунд»
  выполняется по умолчанию, а тесты корректности бизнес-логики передают `TimeSpan.Zero`
  («задержка нужна только для проведения эксперимента»).
* `async`/`await Task.Delay(...)` вместо `Thread.Sleep(...)`: `Task.Delay` **не занимает поток** на время
  ожидания, а `Thread.Sleep` заблокировал бы поток пула. `await` в методе, объявленном `async Task<Response>`,
  возвращает управление вызывающему коду, а продолжение выполнится после истечения задержки.
* Задержка стоит **между** проверкой `product.Stock < quantity` и записью `product.Stock -= quantity` —
  именно это и требует Требование 6.
* `HandleAsync` вызывает `await CreateOrderAsync(request.Data)` прямо внутри `switch`-выражения — `await`
  допустим в ветвях `switch`-выражения внутри `async`-метода.

### Эксперимент 1 (Требование 5): две клиентки, одна операция

Порядок: запускаем сервер и двух клиентов, оба создают заказ на товар `productId = 1` (`Ноутбук`),
второй клиент — через 1 секунду после первого.

Реальный вывод сервера:

```
[18:16:38.163] Client 1 START | Operation: CreateOrder | Task: 1 | Thread: 7
[18:16:39.129] Client 2 START | Operation: CreateOrder | Task: 2 | Thread: 5
[18:16:48.178] Client 1 END | Operation: CreateOrder | Task: 1 | Thread: 7 | Elapsed: 10,0 s
[18:16:49.130] Client 2 END | Operation: CreateOrder | Task: 2 | Thread: 5 | Elapsed: 10,0 s
```

Ответы клиентов: оба `"Success":true, "Event":"OrderCreated"` (заказы `Id: 1` и `Id: 2`).

Анализ:

* `Client 2 START` (18:16:39.129) произошёл **до** `Client 1 END` (18:16:48.178) — обработка запросов
  **перекрывается во времени**, то есть сервер работал параллельно.
* Общее время эксперимента ≈ 11 с (с 18:16:38.163 по 18:16:49.130), а не 20 с. При последовательной обработке
  второй запрос ждал бы завершения первого, и суммарное время было бы ≈ 20 с.
* Каждый клиент обрабатывался своей задачей (`Task: 1` и `Task: 2`) — это и есть доказательство
  параллельности, полученное из временных меток, а не из факта «запущено два клиента».

## 6. Требование 6. Конкурентный доступ к общему ресурсу, Race Condition

Общий ресурс — количество товара на складе. Один экземпляр `BusinessLogic` создаётся один раз в
конструкторе сервера и используется **всеми** задачами клиентов, поэтому список товаров и поле
`Product.Stock` — общие данные для всех задач:

```csharp
private readonly List<Product> products =
[
    new Product(1, "Ноутбук", 85000) { Stock = 5 },
    new Product(2, "Смартфон", 55000) { Stock = 8 },
    new Product(3, "Наушники", 7000) { Stock = 12 },
    new Product(4, "Планшет", 30000) { Stock = 1 }   // ресурс для эксперимента: осталась 1 единица
];
```

Никаких `lock`, `Interlocked`, `SemaphoreSlim` и других средств синхронизации в проекте нет — по заданию
проблему нужно **продемонстрировать**, а не исправить. Опасный участок:

```
1. Server не знает ничего о состоянии клиентов.
2. Client 1: проверка  if (product.Stock < 1)  -> 1 < 1 = false -> "можно покупать"
3. Client 1: await Task.Delay(10 s)                     <- 10 секунд "пустой" работы
4. Client 2: проверка  if (product.Stock < 1)  -> всё ещё 1 -> "можно покупать"
5. Client 1: product.Stock -= 1  -> 1 - 1 = 0
6. Client 2: product.Stock -= 1  -> 0 - 1 = -1           <- ПРОДАЛИ ТОВАР, КОТОРОГО НЕТ
```

### Эксперимент 2 (Требование 6): две клиентки покупают последний товар

Порядок: сервер запущен, товар `productId = 4` имеет `Stock = 1`; `Client 1` создаёт заказ через 0 с,
`Client 2` — через 0,5 с (оба внутри окна задержки).

Реальный вывод сервера:

```
[18:16:57.200] Client 3 START | Operation: CreateOrder | Task: 3 | Thread: 7
[18:16:57.699] Client 4 START | Operation: CreateOrder | Task: 4 | Thread: 8
[18:17:07.200] Client 3 END | Operation: CreateOrder | Task: 3 | Thread: 7 | Elapsed: 10,0 s
[18:17:07.700] Client 4 END | Operation: CreateOrder | Task: 4 | Thread: 8 | Elapsed: 10,0 s
[18:17:07.701] Client 5 START | Operation: GetProducts | Task: 5 | Thread: 7
[18:17:07.701] Client 5 END | Operation: GetProducts | Task: 5 | Thread: 7 | Elapsed: 0,0 s
```

Ответы клиентов — **оба успешные**:

```
Client 1 (18:16:57): {"Success":true,"Message":"Заказ создан","Data":{"Id":3,...,"ProductId":4,...},"Event":"OrderCreated"}
Client 2 (18:16:57): {"Success":true,"Message":"Заказ создан","Data":{"Id":4,...,"ProductId":4,...},"Event":"OrderCreated"}
```

Состояние общего ресурса после эксперимента (`GetProducts`):

```
{"Id":1,...,"Stock":3},{"Id":2,...,"Stock":8},{"Id":3,...,"Stock":12},{"Id":4,"Name":"Планшет",...,"Stock":-1}
```

Анализ (это и есть ответ на вопрос «почему так произошло?»):

* Оба клиента прочитали `Stock = 1` **до** того, как кто-либо его изменил (клиенты пришли в разные задачи
  `Task: 3` и `Task: 4`, проверка выполнилась до паузы `Task.Delay`).
* Дальше каждый независимо вычел единицу из **уже устаревшего** значения: `1 - 1 = 0`, затем `0 - 1 = -1`.
* Итог: продано 2 единицы при наличии одной, склад ушёл в минус, и оба клиента получили `SUCCESS` —
  типичный Race Condition «проверь-и-действуй» (check-then-act) на непротектированном общем ресурсе.
* Результат зависит от порядка и моментов выполнения операций: если бы клиенты пришли не в окно задержки,
  второй получил бы `Недостаточно товара на складе`.

**Проблема намеренно оставлена неисправленной** — именно её устраняют в ЛР №3 механизмами синхронизации.

## 7. Требование 7. Итоговое поведение сервера (чек-лист)

| Требование                                                                                            | Как выполнено                                                                                                                                           |
| --------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| принимать несколько клиентов                                                          | `while` + `await AcceptTcpClientAsync` в `Server.RunAsync`                                                                                                   |
| обрабатывать клиентов в отдельных`Task`                                         | `_ = Task.Run(() => new RequestHandler(...).HandleAsync(client))`                                                                                                 |
| не ждать завершения одного клиента перед приёмом следующего | задачу клиента никто не`await`-ит, цикл приёма продолжается сразу                                                |
| выводить информацию о начале и завершении обработки                | логи`Client N START` и `Client N END`                                                                                                                      |
| выводить временные метки                                                                  | `Timestamp()` = `DateTime.Now.ToString("HH:mm:ss.fff")`                                                                                                         |
| корректно обрабатывать отключение клиента                                 | `SendResponseAsync` ловит исключение отправки, пишет в лог `ОТКЛЮЧЁН`, задача завершается штатно |
| корректно обрабатывать ошибки                                                        | `catch (Exception)` вокруг бизнес-логики, ответ `Ошибка обработки запроса`, сервер жив                    |
| позволять провести оба эксперимента                                             | задержка 10 с в`CreateOrderAsync` + товар с `Stock = 1`                                                                                         |

## 8. Требование 8. Клиент

Клиент ЛР №1 уже удовлетворяет требованию, поэтому остался без изменений (`Client/Client.cs`):

```csharp
public sealed class Client(string host = "127.0.0.1", int port = 5000)
{
    public async Task<Response> SendAsync(Request request)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream);
            using var writer = new StreamWriter(stream) { AutoFlush = true };
            await writer.WriteLineAsync(JsonProtocol.Serialize(request));
            return JsonProtocol.Deserialize<Response>(await reader.ReadLineAsync() ?? "")
                ?? new(false, "Пустой ответ сервера");
        }
        catch (SocketException)
        {
            return new(false, "Сервер недоступен");
        }
        catch (IOException)
        {
            return new(false, "Ошибка сетевого взаимодействия");
        }
    }
}
```

* подключается к серверу (`ConnectAsync`), отправляет запрос (`WriteLineAsync` JSON-строкой),
  получает ответ (`ReadLineAsync`), отображает результат (`Program.PrintResponse`);
* «одновременно минимум два клиента» — это два процесса клиента (`dotnet run --project Client/...` в двух
  терминалах): каждый создаёт свой `TcpClient`, и сервер принимает их разными задачами;
* `host`/`port` имеют значения по умолчанию, поэтому дополнительные параметры передавать не нужно;
* ошибки сети не выбрасываются наружу, а превращаются в обычный `Response` с `Success = false`.

## 9. Требование 9. Обработка ошибок

| Ситуация                                                         | Где обрабатывается                                                                                                    | Как проверено и что получилось                                                                                                                                                                                                                                                                         |
| ------------------------------------------------------------------------ | -------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Клиент отключился во время работы           | `SendResponseAsync` (`catch` вокруг отправки) + `using var`                                                        | тест`ClientDisconnectsDuringProcessing_ServerKeepsRunning`: клиент шлёт `CreateOrder` и сразу закрывает сокет; сервер дорабатывает 10 с, логирует `Client 1 END` и `ОТКЛЮЧЁН`, затем продолжает обслуживать других |
| Сервер недоступен                                        | `Client.SendAsync`, `catch (SocketException)`                                                                                      | тест`ServerUnavailable_ClientReturnsError`: клиент к свободному (закрытому) порту: `Success = false`, сообщение `Сервер недоступен`                                                                                                                            |
| Клиент отправил некорректный запрос      | `ReadRequestAsync` (JSON) + `BusinessLogic.HandleAsync` (пустой `Operation`)                                               | тест`InvalidRequest_ServerReturnsErrorAndStaysAlive`: строка «это не json»: `Success = false`, следующий корректный запрос всё равно проходит                                                                                                                      |
| Запрашиваемый ресурс не существует        | `CreateOrderAsync` (`Товар не найден`), `PayOrder`/`CancelOrder` (`Заказ не найден`)               | тест`MissingResource_ServerReturnsError`: `productId = 999` и `orderId = 999`: `Success = false` с конкретными сообщениями                                                                                                                                                                   |
| Произошла ошибка при обработке запроса | `catch (Exception)` в `RequestHandler.HandleAsync`, в ответ уходит `Ошибка обработки запроса` | изолированность задачи клиента: исключение внутри задачи не завершает сервер, а логируется как `Client N ERROR                                                                                                                                  |

Главное требование — «сервер не должен аварийно завершаться из-за ошибки одного клиента» — обеспечивается
тремя точками перехвата: разбор запроса, бизнес-логика и отправка ответа. Исключение в задаче клиента
заканчивается только этой задачей: цикл приёма подключений (`Server.RunAsync`) находится в отдельной задаче
и продолжает работу.

Дополнительно, по вашему решению, приведён в соответствие с реальным поведением сервера перенесённый из
ЛР №1 тест доступности (`Tests/ServerAvailabilityTests.cs`) — теперь он проверяет настоящий ответ на
`GetProducts`:

```csharp
var response = await SendRequestAsync(port, new Request("GetProducts"));

Assert.True(response.Success);
Assert.NotNull(response.Data);
```

## 10. Требование 10. Тестирование (Unit-тестирование)

Обязательные четыре теста ЛР №2 разложены по классам:

| № | Тест (Требование 10)                                                   | Класс / метод                                         |
| -- | ------------------------------------------------------------------------------------ | --------------------------------------------------------------- |
| 1  | Один клиент — корректность обычной обработки  | `ParallelProcessingTests.OneClient_RequestIsProcessed`        |
| 2  | Два клиента — сервер обрабатывает оба запроса | `ParallelProcessingTests.TwoClients_BothRequestsAreProcessed` |
| 3  | Параллельная обработка (+10 с, сравнение меток)  | `ParallelProcessingTests.TwoClients_AreProcessedInParallel`   |
| 4  | Race Condition (`Available = 1`, два одинаковых запроса)       | `RaceConditionTests.TwoClients_BuyLastItem_BothGetSuccess`    |

Чтобы тесты могли проверять временные метки, сервер получил необязательный делегат логирования, а тесты —
вспомогательный класс, который поднимает настоящий сервер на свободном порту и собирает его лог
(`Tests/TestServer.cs`):

```csharp
internal sealed class TestServer : IAsyncDisposable
{
    private readonly ConcurrentQueue<string> logs = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task serverTask;

    public TestServer(TimeSpan? processingDelay = null)
    {
        Port = GetFreePort();
        Logic = new BusinessLogic(processingDelay);
        serverTask = new OnlineStore.Server.Server(Port, Logic, logs.Enqueue).RunAsync(cancellation.Token);
    }

    public int Port { get; }
    public BusinessLogic Logic { get; }
    public IReadOnlyList<string> Logs => logs.ToArray();

    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel();
        await serverTask;
    }

    public static async Task<Response> SendAsync(int port, Request request) =>
        await SendRawAsync(port, JsonProtocol.Serialize(request));

    public static async Task<Response> SendRawAsync(int port, string payload)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream);
        using var writer = new StreamWriter(stream) { AutoFlush = true };
        await writer.WriteLineAsync(payload);
        return JsonProtocol.Deserialize<Response>(await reader.ReadLineAsync() ?? "")
            ?? new(false, "Пустой ответ сервера");
    }

    public async Task<bool> WaitForLogAsync(Func<string, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !Logs.Any(predicate))
            await Task.Delay(100);
        return Logs.Any(predicate);
    }

    public static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
```

Что здесь важно:

* `new OnlineStore.Server.Server(Port, Logic, logs.Enqueue)` — `logs.Enqueue` это method group, который
  превращается в делегат `Action<string>`; так тесты получают тот же текст, что печатается в консоль.
* `GetFreePort()` — `TcpListener` на порту `0` просит у ОС свободный порт, узнаёт его из
  `((IPEndPoint)listener.LocalEndpoint).Port` и сразу освобождает: тесты не конфликтуют друг с другом.
* `IAsyncDisposable` + `await using` — сервер корректно останавливается `CancellationTokenSource.Cancel()`
  и `await serverTask` в конце теста (иначе порт и задача остались бы висеть).
* `WaitForLogAsync` опрашивает лог с шагом 100 мс и ограничением по времени — нужен там, где сервер
  печатает строку асинхронно (например, `END` после 10-секундной обработки).
* `ConcurrentQueue<string>` — лог пишут задачи сервера из разных потоков, поэтому коллекция потокобезопасна.

### Тесты 1–3. Один клиент, два клиента, параллельная обработка

`Tests/ParallelProcessingTests.cs`:

```csharp
public sealed class ParallelProcessingTests
{
    private static readonly Dictionary<string, string> OrderData = new()
    {
        ["customerId"] = "1", ["productId"] = "1", ["quantity"] = "1"
    };

    [Fact]
    public async Task OneClient_RequestIsProcessed()
    {
        await using var server = new TestServer(TimeSpan.Zero);

        var response = await TestServer.SendAsync(server.Port, new Request("CreateOrder", OrderData));

        Assert.True(response.Success);
        Assert.Equal("OrderCreated", response.Event);
        Assert.Equal(4, server.Logic.Products.Single(product => product.Id == 1).Stock);
        Assert.Contains(server.Logs, line => line.Contains("Client 1 START"));
        Assert.Contains(server.Logs, line => line.Contains("Client 1 END"));
    }

    [Fact]
    public async Task TwoClients_BothRequestsAreProcessed()
    {
        await using var server = new TestServer(TimeSpan.Zero);

        var responses = await Task.WhenAll(
            TestServer.SendAsync(server.Port, new Request("GetProducts")),
            TestServer.SendAsync(server.Port, new Request("CreateOrder", OrderData)));

        Assert.All(responses, response => Assert.True(response.Success));
        Assert.Contains(server.Logs, line => line.Contains("Client 1 END"));
        Assert.Contains(server.Logs, line => line.Contains("Client 2 END"));
    }

    [Fact]
    public async Task TwoClients_AreProcessedInParallel()
    {
        await using var server = new TestServer();

        var first = TestServer.SendAsync(server.Port, new Request("CreateOrder", OrderData));
        await Task.Delay(TimeSpan.FromSeconds(1));
        var second = TestServer.SendAsync(server.Port, new Request("CreateOrder", OrderData));
        var responses = await Task.WhenAll(first, second);

        var firstStart = ParseTimestamp(server.Logs.First(line => line.Contains("Client 1 START")));
        var firstEnd = ParseTimestamp(server.Logs.First(line => line.Contains("Client 1 END")));
        var secondStart = ParseTimestamp(server.Logs.First(line => line.Contains("Client 2 START")));

        Assert.All(responses, response => Assert.True(response.Success));
        Assert.True((firstEnd - firstStart).TotalSeconds >= 10);
        Assert.True(secondStart < firstEnd);
    }

    private static DateTime ParseTimestamp(string line) =>
        DateTime.ParseExact(line[1..13], "HH:mm:ss.fff", CultureInfo.InvariantCulture);
}
```

Что здесь важно:

* `new TestServer(TimeSpan.Zero)` — в тестах 1 и 2 задержка выключена: проверяется **корректность**
  обработки, а не параллельность, и ждать 10 секунд в каждом тесте не нужно. В тесте 3 задержка
  **по умолчанию** (10 с) — она и создаёт перекрытие во времени.
* `Task.WhenAll(first, second)` — оба клиента работают одновременно, тест ждёт завершения **обоих**.
  Запросы получены до `await` вызовов: так клиенты действительно запускаются параллельно,
  а не последовательно после ожидания первого.
* Метки времени вытаскиваются из лога сервера регуляркой по подстроке (`Client 1 START`) и
  разбираются `DateTime.ParseExact(line[1..13], "HH:mm:ss.fff", ...)`; `line[1..13]` — это диапазон
  (range) по строке, эквивалент `line.Substring(1, 12)`, отделяющий время из `[18:16:38.163]`.
* Ключевая проверка теста 3 — `secondStart < firstEnd`: **второй клиент начал обработку раньше, чем
  первый закончил**, то есть запросы перекрылись во времени (при последовательной обработке это
  утверждение было бы ложным).
* `server.Logic.Products` — тот же экземпляр бизнес-логики, что и у сервера, поэтому состояние общего
  ресурса (склад) проверяется напрямую, без разбора JSON.
* `Assert.Equal(4, ...)` — «Ноутбук» имел 5 единиц, после одной покупки стало 4.

### Тест 4. Race Condition

`Tests/RaceConditionTests.cs`:

```csharp
public sealed class RaceConditionTests
{
    [Fact]
    public async Task TwoClients_BuyLastItem_BothGetSuccess()
    {
        await using var server = new TestServer();
        var request = new Request("CreateOrder", new Dictionary<string, string>
        {
            ["customerId"] = "1", ["productId"] = "4", ["quantity"] = "1"
        });

        Assert.Equal(1, server.Logic.Products.Single(product => product.Id == 4).Stock);

        var responses = await Task.WhenAll(
            TestServer.SendAsync(server.Port, request),
            TestServer.SendAsync(server.Port, request));

        Assert.All(responses, response => Assert.True(response.Success));
        Assert.All(responses, response => Assert.Equal("OrderCreated", response.Event));
        Assert.Equal(-1, server.Logic.Products.Single(product => product.Id == 4).Stock);
    }
}
```

Что здесь важно:

* Перед экспериментом проверяется исходное условие задания: `Assert.Equal(1, ...Stock)` — «доступна
  ровно 1 единица ресурса» (товар `Планшет`, `productId = 4`).
* Один и тот же объект `request` отправляется **двумя разными соединениями**: оба запроса попадают в окно
  задержки 10 с, поэтому обе задачи видят ещё не изменённый склад.
* `Assert.All(responses, ...)` — два утверждения по коллекции ответов: оба клиента получили `SUCCESS` и
  событие `OrderCreated`.
* `Assert.Equal(-1, ...Stock)` — анализ состояния общего ресурса: продано 2 единицы при наличии 1,
  склад ушёл в минус. Тест **фиксирует наличие Race Condition** (в ЛР №3 он будет переписан так, чтобы
  ожидать ровно один `SUCCESS` и `Stock = 0`).
* Так как используется настоящий сервер и реальный TCP, тест одновременно доказывает и то, что сервер
  корректно работает с несколькими подключениями.

### Тесты обработки ошибок (Требование 9)

`Tests/ErrorHandlingTests.cs`:

```csharp
public sealed class ErrorHandlingTests
{
    private static readonly Dictionary<string, string> OrderData = new()
    {
        ["customerId"] = "1", ["productId"] = "1", ["quantity"] = "1"
    };

    [Fact]
    public async Task ServerUnavailable_ClientReturnsError()
    {
        var response = await new OnlineStore.Client.Client(port: TestServer.GetFreePort())
            .SendAsync(new Request("GetProducts"));

        Assert.False(response.Success);
        Assert.Equal("Сервер недоступен", response.Message);
    }

    [Fact]
    public async Task InvalidRequest_ServerReturnsErrorAndStaysAlive()
    {
        await using var server = new TestServer(TimeSpan.Zero);

        var invalid = await TestServer.SendRawAsync(server.Port, "это не json");
        var valid = await TestServer.SendAsync(server.Port, new Request("GetProducts"));

        Assert.False(invalid.Success);
        Assert.True(valid.Success);
    }

    [Fact]
    public async Task MissingResource_ServerReturnsError()
    {
        await using var server = new TestServer(TimeSpan.Zero);

        var missingProduct = await TestServer.SendAsync(server.Port, new Request("CreateOrder", new Dictionary<string, string>
        {
            ["customerId"] = "1", ["productId"] = "999", ["quantity"] = "1"
        }));
        var missingOrder = await TestServer.SendAsync(server.Port, new Request("PayOrder", new Dictionary<string, string>
        {
            ["orderId"] = "999"
        }));

        Assert.False(missingProduct.Success);
        Assert.Equal("Товар не найден", missingProduct.Message);
        Assert.False(missingOrder.Success);
        Assert.Equal("Заказ не найден", missingOrder.Message);
    }

    [Fact]
    public async Task ClientDisconnectsDuringProcessing_ServerKeepsRunning()
    {
        await using var server = new TestServer();

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, server.Port);
            client.Client.LingerState = new LingerOption(true, 0);
            await using var stream = client.GetStream();
            await using var writer = new StreamWriter(stream) { AutoFlush = true };
            await writer.WriteLineAsync(JsonProtocol.Serialize(new Request("CreateOrder", OrderData)));
            await server.WaitForLogAsync(line => line.Contains("Client 1 START"), TimeSpan.FromSeconds(5));
        }

        var response = await TestServer.SendAsync(server.Port, new Request("GetProducts"));

        Assert.True(response.Success);
        Assert.True(await server.WaitForLogAsync(line => line.Contains("Client 1 END"), TimeSpan.FromSeconds(15)));
        Assert.Contains(server.Logs, line => line.Contains("Client 1 ОТКЛЮЧЁН"));
    }
}
```

Что здесь важно:

* «Сервер недоступен» проверяется **настоящим клиентом ЛР №1** (`OnlineStore.Client.Client`):
  порт после `GetFreePort()` никем не занят, `ConnectAsync` даёт `SocketException`, который клиент
  превращает в `Response(false, "Сервер недоступен")` — приложение не падает.
* `TimeSpan.Zero` — там, где проверяется реакция на ошибку, задержка не нужна.
* В тесте об отключении: `LingerOption(true, 0)` заставляет ОС отправить `RST` при закрытии сокета, поэтому
  сервер гарантированно получит ошибку при попытке отправить ответ; `using (var client = ...)` закрывает
  соединение **сразу после** того, как в логе появился `START`, то есть клиент отключается **во время**
  10-секундной обработки. Проверки: сервер всё равно дописал `END`, зафиксировал `ОТКЛЮЧЁН` и обслужил
  следующий запрос (в ответ на `GetProducts` приходит `Success = true`).
* `await server.WaitForLogAsync(...)` вместо `Thread.Sleep(10000)` — ожидание конкретного события с
  ограничением по времени, поэтому тест не «висит» и не ждёт лишнего.

### Результат прогона всех тестов

```
dotnet test
Пройден!   : не пройдено     0, пройдено    18, пропущено     0, всего    18, длительность 11 s.
```

18 тестов = 9 тестов бизнес-логики (ЛР №1, переведены на `async`) + 1 тест доступности сервера (ЛР №1) +
3 теста параллельной обработки + 1 тест Race Condition + 4 теста обработки ошибок.

## 11. Особенности языка C# и значения использованных функций и параметров

| Конструкция                              | Что делает / значение                                                                                                                                                                                 | Почему так                                                                                                                                    |
| --------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `Task.Run(() => handler.HandleAsync(client))`     | Ставит работу в очередь Thread Pool и возвращает`Task`, «развёрнутый» из `Func<Task>` (перегрузка `Task.Run(Func<Task>)`)                                | Перегрузка`Task.Run(Action)` подошла бы только для синхронного кода                                       |
| `async Task<Response> HandleAsync(...)`           | Метод компилируется в асинхронный автомат состояний; возвращаемый`Task<Response>` завершится, когда метод дойдёт до конца | `async void` использовать нельзя — исключения нельзя отследить                                           |
| `await Task.Delay(processingDelay)`               | Асинхронная пауза, поток на это время возвращается в пул                                                                                                                | `Thread.Sleep(10_000)` заблокировал бы поток пула                                                                             |
| `Task.CurrentId`                                  | `int?` — номер текущей задачи; `null` вне тела задачи и после `await`                                                                                                      | Поэтому значение сохраняется в локальную переменную`taskId` в начале метода               |
| `Environment.CurrentManagedThreadId`              | `int` — номер управляемого потока; всегда доступен                                                                                                                             | Позволяет показать, что продолжение после`await` может выполниться в другом потоке  |
| `Interlocked.Increment(ref clientCounter)`        | Атомарно увеличивает счётчик и**возвращает новое значение** (1, 2, 3, …)                                                                                      | Обычный`clientCounter++` не атомарен, поэтому два клиента могли получить один номер          |
| `Action<string> log`                              | Делегат «принять строку, ничего не вернуть»                                                                                                                                       | Внедрение логирования: по умолчанию`Console.WriteLine`, в тестах — сбор в коллекцию             |
| `log ?? Console.WriteLine`                        | Оператор`??` возвращает правый операнд, если левый `null`                                                                                                                  | Так значение по умолчанию задаётся прямо в поле                                                                |
| `TimeSpan? processingDelay = null`                | `Nullable<TimeSpan>` — «задержка либо задана, либо `null`»                                                                                                                                | при`null` берётся `DefaultProcessingDelay = TimeSpan.FromSeconds(10)`                                                                    |
| `TimeSpan.Zero`                                   | Нулевой интервал, поэтому`Task.Delay` завершается сразу                                                                                                                        | Задержка нужна только для экспериментов (Требование 4)                                                    |
| `IReadOnlyList<Product> Products => products`     | Ссылка «только для чтения» на список товаров (сам список внутри изменяемый)                                                                             | Позволяет тестам и отчёту анализировать состояние общего ресурса                              |
| `using var connection = client;`                  | `IDisposable` освобождается при выходе из метода даже при исключении                                                                                                  | Гарантированное закрытие сокета/потоков                                                                            |
| `await using var server = new TestServer();`      | Работа с`IAsyncDisposable` — асинхронное освобождение                                                                                                                                 | Сервер корректно останавливается (`CancellationTokenSource.Cancel()` + `await serverTask`)                           |
| `_ = Task.Run(...)`                               | Discard-переменная: результат сознательно игнорируется (fire-and-forget)                                                                                                     | Серверу не нужна ссылка на задачу клиента, а исключения обрабатываются внутри        |
| `Task.WhenAll(a, b)`                              | Ждёт завершения всех задач и возвращает массив результатов                                                                                                          | Используется и в тесте параллельности, и при двух клиентах                                            |
| `CancellationToken` / `CancellationTokenSource` | Кооперативная отмена:`AcceptTcpClientAsync(token)` бросает `OperationCanceledException` при отмене                                                                              | Штатная остановка сервера в тестах и при завершении приложения                                   |
| `ConcurrentQueue<string>`                         | Потокобезопасная очередь (без блокировок)                                                                                                                                          | Лог пишут задачи из разных потоков                                                                                        |
| `record Product(int Id, ... , { get; set; })`     | `record` даёт сравнение по значению и `with`; изменяемое свойство добавлено для склада                                                                 | Склад — изменяемый общий ресурс, а`Product` остаётся неизменяемым по остальным полям |
| `request?.Operation ?? "Unknown"`                 | `?.` — null-conditional, `??` — значение по умолчанию                                                                                                                                         | Некорректный JSON даёт`null`-запрос                                                                                            |
| `line[1..13]`                                     | Range-оператор: подстрока с индексами 1…12                                                                                                                                                 | Выделяет`HH:mm:ss.fff` из `[...]`                                                                                                        |
| `Stopwatch.StartNew()`, `Elapsed.TotalSeconds`  | Точный замер длительности обработки                                                                                                                                                    | Дает поле`Elapsed` в логе                                                                                                               |
| `switch`-выражение с `await`          | `"CreateOrder" => await CreateOrderAsync(request.Data)`                                                                                                                                                              | `await` допустим в ветвях выражения внутри `async`-метода                                                      |
| `[Fact]`, `Assert.True/Equal/Contains`          | Атрибут теста и утверждения xUnit                                                                                                                                                              | Автоматическая проверка ЛР №2                                                                                                 |

## 12. Теоретическая часть для защиты

### 12.1. Process, Thread, Task

* **Процесс (Process)** — выполняемая программа, которая имеет собственное адресное пространство и системные ресурсы.
* **Задача (Task)** — абстракция «асинхронной работы» в .NET. 
* Отличия: поток — сущность ОС с собственным стеком и дорогим созданием (порядка сотен тысяч тактов);
  задача — сущность уровня .NET, которая переиспользует потоки пула и стоит на порядок дешевле.
  Поток всегда «занимает» исполнителя, задача — нет.

**Поток**  - последовательность выполнения внутри процесса.

Один процесс может содержать несколько потоков, которые выполняются независимо или совместно работают с одними и теми же данными.


### 12.2. Последовательное и параллельное выполнение

* **Последовательное:** сервер обрабатывает запрос за запросом. Пока не закончится обработка Client 1
  (10 с), Client 2 ждёт. Общее время ≈ 10 + 10 = **20 с**.
* **Параллельное:** сервер запускает обработку Client 2, не дожидаясь окончания Client 1 — обработки
  **перекрываются во времени**. Общее время ≈ **10 с** (в нашем эксперименте 11 с: 1 с между запусками
  клиентов + 10 с обработки).
* Формальное условие параллельности: `START(Client 2) < END(Client 1)`. Именно это утверждение
  проверяется и в логе эксперимента, и в тесте №3.

### 12.3. Task.Run, async/await, Thread Pool

* `Task.Run(...)` — «выполни это в потоке из Thread Pool и верни задачу». 
* `await` — точка «передать управление, не блокируя поток». Компилятор превращает `async`-метод в автомат
  состояний: до первого `await` код выполняется на текущем потоке, после `await` продолжение выполняет
  тот поток, который завершит ожидаемую задачу (нередко другой поток из пула — это видно в логах).
* **Thread Pool** — пул заранее созданных потоков .NET. Задачи переиспользуют их, поэтому 100 клиентов не
  создают 100 потоков. Из-за этого `Task.CurrentId` и `Thread.ManagedThreadId` не совпадают, а один поток
  по очереди обслуживает разные задачи.
* Асинхронность ≠ параллельность: `async/await` про **неблокирующее ожидание** (I/O, сеть, задержки),
  параллельность — про **одновременное выполнение**. В этой ЛР используются оба приёма: `Task.Run`
  (параллельность обработки клиентов) + `async/await` (неблокирующее ожидание сокета и задержки).

### 12.4. Общий ресурс

**Общий ресурс** — данные, к которым обращаются несколько потоков/задач. В этой работе общий ресурс —
`BusinessLogic` (один экземпляр на весь сервер) и, в частности, поле `Product.Stock`
(«Количество товара на складе», вариант 1). Список `products` и поле `Stock` читаются и изменяются
из разных задач: `GetProducts` читает, `CreateOrder`/`CancelOrder` изменяют.

### 12.5. Race Condition

**Race Condition (состояние гонки)** — ситуация, когда результат работы программы зависит от порядка и
моментов выполнения параллельных операций над общими данными. Классическая форма — **check-then-act**:
сначала проверяем условие, затем действуем. Если между проверкой и действием другой поток успевает
изменить данные, решение принимается по устаревшей информации.

В нашем коде:

```
проверка:  if (product.Stock < quantity)      // прочитали Stock = 1
пауза:     await Task.Delay(10 s)             // окно для второго клиента
действие:  product.Stock -= quantity          // записали по устаревшим данным
```

### 12.6. Почему Race Condition нельзя игнорировать

* **Продажа того, чего нет.** В нашем эксперименте склад ушёл в `-1`: два клиента оплатили товар,
  которого оставалась одна единица. В реальном магазине это отменённые заказы, штрафы, потеря клиентов.
* **То же самое в других вариантах задания:** двойное бронирование номера/места/билета, два курьера на
  один заказ, два пациента на одно время у врача, один автомобиль на две аренды.
* **Финансовые последствия:** двойные списания/начисления, отрицательные остатки на складе, расхождения
  в отчётности и бухгалтерии.
* **Ошибки «плавающие» и трудно воспроизводимые:** зависят от расписания потоков и задержек сети, поэтому
  проявляются редко, «на нагрузке», и их сложно поймать при тестировании.
* **Некорректное состояние данных** сохраняется навсегда (в БД нет «-1 товара» на реальном складе) и
  обнаруживается уже по факту, вместе с потерей доверия пользователей.
* Чем больше клиентов и чем медленнее операция (внешние платежи, запросы к БД), тем шире окно гонки —
  значит, вероятность проблемы растёт с ростом системы.

### 12.7. Механизмы синхронизации (то, что будет применено в ЛР №3)

* `lock` (Monitor) — самый простой способ сделать участок «проверка + изменение» **критической секцией**:
  пока один поток внутри, остальные ждут. Подходит для нашего `BusinessLogic`.
* `SemaphoreSlim` (в т. ч. `await semaphore.WaitAsync()`) — ограничивает число одновременных исполнителей
  критической секции, умеет работать асинхронно (не блокирует поток), хорошо для `async`-кода.
* `Interlocked` (`Increment`, `Decrement`, `CompareExchange`, `Add`) — атомарные операции над числами;
  `CompareExchange` реализует «условное» изменение: «сделай `Stock - 1`, если там до сих пор 1».
* `Interlocked.CompareExchange` / `Volatile` / `volatile` — для отдельных полей-флагов.
* `ReaderWriterLockSlim` — много читателей одновременно и один писатель (например, `GetProducts` против
  `CreateOrder`).
* `Mutex` — межпроцессная блокировка (нужна, если общий ресурс делят разные процессы/машины).
* Потокобезопасные коллекции (`ConcurrentDictionary`, `ConcurrentQueue`, `BlockingCollection`) — когда
  общий ресурс сам является коллекцией.
* **Транзакции и оптимистическая блокировка в БД** (`SELECT ... FOR UPDATE`, уровни изоляции, версия/`RowVersion`,
  `ETag`, условное `UPDATE ... WHERE Stock >= 1`) — «правильный» способ для распределённых систем, где
  общий ресурс хранится в базе, а не в памяти одного сервера.
* `Channel<T>` / очередь запросов и единственный обработчик — альтернатива блокировкам: весь доступ к
  ресурсу идёт через один поток.
* В распределённой системе (несколько серверов, как в нашей модели с несколькими клиентами) критично,
  что `lock` внутри одного процесса **не защищает** от гонки между разными процессами/серверами —
  поэтому нужны транзакции БД или внешние распределённые блокировки (например, Redis Lock).

## 13. Ответы на контрольные вопросы

1. **Что такое процесс?** Запущенная программа вместе со своим адресным пространством, дескрипторами
   и другими ресурсами ОС. Пример в работе: процесс сервера (`OnlineStore.Server`) и отдельный процесс
   каждого клиента (`OnlineStore.Client`), которые общаются через TCP.
2. **Что такое поток?** Последовательность выполнения внутри процесса; имеет собственный стек и разделяет
   с процессом его память и ресурсы. Пример: задачи клиентов выполняются в потоках Thread Pool
   (`Thread: 7`, `Thread: 8` из логов).
3. **Чем поток отличается от процесса?** Процесс изолирован (своё адресное пространство, свои ресурсы,
   свой PID), поток существует внутри процесса и делит с другими его потоками память/файлы/сокеты. Поэтому
   потоки «видят» общий ресурс, а процессы — нет (им нужно IPC или сеть). Создать поток дешевле процесса,
   а переключение потоков быстрее переключения процессов.
4. **Что такое Task в C#?** Абстракция асинхронной операции: представляет работу, которая когда-то
   завершится, может вернуть результат (`Task<T>`) или ошибку. Задача выполняется потоком из Thread Pool
   или не занимает поток вовсе (`Task.Delay`). Пример: `HandleAsync` возвращает `Task`,
   `Server.RunAsync` возвращает `Task`; каждый клиент обрабатывается в задаче от `Task.Run`.
5. **Для чего используется `Task.Run()`?** Чтобы поставить выполнение кода в очередь Thread Pool и получить
   задачу для отслеживания. В работе это ключевой приём Требования 2: `_ = Task.Run(() => new RequestHandler(...).HandleAsync(client))` —
   сервер не ждёт клиента и продолжает принимать подключения.
6. **Чем последовательное выполнение отличается от параллельного?** При последовательном запросы
   обрабатываются один за другим (время суммируется: 10 + 10 = 20 с), при параллельном обработка
   перекрывается во времени (общее время ≈ 10 с, максимум длительностей).
7. **Почему простого запуска двух клиентов недостаточно, чтобы доказать параллельную обработку?**
   Потому что быстрый запрос может быть обработан целиком одним или другим потоком «по очереди», и по
   факту «запущено два клиента» нельзя сказать, ждали ли они друг друга. Нужны **временные метки**:
   параллельность доказывает перекрытие интервалов `[START; END]`, то есть `START(Client 2) < END(Client 1)`.
   Именно поэтому добавляется искусственная задержка, делающая перекрытие наблюдаемым.
8. **Какие данные в логах позволяют доказать, что два запроса выполнялись одновременно?** Временные метки
   `START`/`END` с миллисекундами и номера задач/потоков. Доказательство: `Client 2 START 18:16:39.129`
   **раньше** `Client 1 END 18:16:48.178` (интервалы перекрылись), плюс разные задачи `Task: 1` и `Task: 2`,
   работавшие в разных потоках. Дополнительно суммарное время ≈ 11 с вместо 20 с.
9. **Зачем в эксперименте используется искусственная задержка?** Чтобы сделать окно перекрытия
   наблюдаемым: быстрые запросы выполняются за миллисекунды, и по логам нельзя отличить параллельную
   обработку от последовательной. Задержка 10 с также создаёт окно между проверкой доступности ресурса и
   его изменением, без которого Race Condition почти невозможно поймать детерминированно.
10. **Что такое общий ресурс?** Данные, к которым обращаются несколько потоков/задач. В работе это
    `BusinessLogic` с полем `Product.Stock` (количество товара на складе): `GetProducts` читает,
    `CreateOrder` и `CancelOrder` изменяют; доступ идёт из разных задач одновременно.
11. **Что такое Race Condition?** Ситуация, когда результат зависит от порядка и моментов выполнения
    параллельных операций над общими данными. Форма «проверь-и-действуй»: два потока прочитали
    `Stock = 1`, оба решили, что товар есть, и оба списали единицу.
12. **Почему два клиента могли получить SUCCESS, если ресурс был только один?** Потому что между проверкой
    `if (product.Stock < quantity)` и изменением `product.Stock -= quantity` стоит `await Task.Delay(10 s)`.
    Обе задачи выполнили проверку до первого изменения и приняли решение по одному и тому же значению
    `Stock = 1`. Дальше первая записала `0`, вторая — уже по устаревшим данным — `-1`. Оба ответа
    сформированы успешными, потому что обе проверки прошли; итоговое состояние ресурса некорректно.
13. **Почему проблему Race Condition нельзя игнорировать в настоящей распределённой системе?**
    Она приводит к продаже/бронированию того, чего нет, двойным списаниям и отрицательным остаткам,
    то есть к реальному ущербу и повреждению данных. Проявляется редко, зависит от расписания потоков и
    задержек сети, поэтому её тяжело воспроизвести и найти; при росте нагрузки вероятность растёт.
    В распределённой системе это ещё опаснее: `lock` внутри одного процесса не защищает от гонки между
    несколькими серверами, которые работают с общим ресурсом в БД.
14. **Какие механизмы синхронизации могут использоваться для решения проблемы Race Condition?**
    `lock`/`Monitor` (критическая секция), `SemaphoreSlim` (в т. ч. `await WaitAsync()`), атомарные
    операции `Interlocked` (например `CompareExchange` — «списать, только если там всё ещё 1»),
    `ReaderWriterLockSlim`, `Mutex` (межпроцессный), потокобезопасные коллекции и `Channel<T>`,
    а также транзакции и оптимистическая блокировка в БД (`SELECT ... FOR UPDATE`, `RowVersion`,
    условный `UPDATE ... WHERE Stock >= 1`), распределённые блокировки (Redis Lock).

## 14. Как воспроизвести оба эксперимента вручную

1. Запустить сервер: `dotnet run --project Server/OnlineStore.Server.csproj`.
2. **Эксперимент 1 (параллельность).** В двух терминалах запустить клиентов и в обоих выбрать
   `2` (создать заказ), ID товара `1`, количество `1`; второй клиент — в течение 10 секунд после первого.
   В логе сервера убедиться, что `Client 2 START` напечатан **раньше**, чем `Client 1 END`.
3. **Эксперимент 2 (гонка).** Перезапустить сервер (склад снова станет `Stock = 1` для товара 4) и в двух
   клиентах выбрать `2`, ID товара `4`, количество `1` (второй — в течение 10 секунд). Оба клиента
   получат «Заказ создан», а пункт `1` (товары) покажет `"Id":4 ... "Stock":-1`.

## 15. Выводы

* Сервер из ЛР №1 стал **многопоточным**: подключения принимаются в цикле, а обработка каждого клиента
  запускается в отдельной `Task` через `Task.Run`, поэтому сервер не блокируется на одном клиенте.
* Параллельная обработка **экспериментально подтверждена** временными метками: `Client 2 START`
  произошёл раньше `Client 1 END`, суммарное время ~11 с вместо ~20 с при последовательной обработке.
* Задержка 10 с (`await Task.Delay`) — необходимый инструмент эксперимента: она делает окно перекрытия
  и окно гонки наблюдаемыми.
* Конкурентный доступ двух задач к общему ресурсу (`Product.Stock`) **воспроизводит Race Condition**:
  оба клиента получили `SUCCESS`, а склад стал `-1`, то есть продан товар, которого не было.
* Проблема **специально оставлена неисправленной** в соответствии с заданием; механизмы синхронизации
  (`lock`, `SemaphoreSlim`, `Interlocked`, транзакции БД) будут применены в ЛР №3.
* Приложение устойчиво к ошибкам: недоступный сервер, некорректный запрос, несуществующий ресурс и
  отключение клиента во время обработки не приводят к аварийному завершению сервера.
* Автотесты (18 шт.) проходят полностью и включают все четыре обязательных теста ЛР №2.

## 16. Структура проекта

```
lab-002/
├── Client/            клиент ЛР №1 без изменений (Client.cs, Program.cs, RequestBuilder.cs)
├── Server/            Server.cs, RequestHandler.cs, BusinessLogic.cs, Program.cs
├── Shared/            Contracts.cs (Request/Response/Product/Order/Payment + JSON-протокол)
├── Tests/             BusinessLogicTests.cs, ServerAvailabilityTests.cs, TestServer.cs,
│                      ParallelProcessingTests.cs, RaceConditionTests.cs, ErrorHandlingTests.cs
├── OnlineStore.slnx   решение
└── README.md          этот файл
```

Операции домена (вариант 1): `GetProducts`, `CreateOrder`, `CancelOrder`, `PayOrder`;
события: `OrderCreated`, `OrderCancelled`, `PaymentCompleted`.
