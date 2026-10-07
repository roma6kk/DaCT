# ЛР №4 — Распределённая работа с базой данных и транзакциями (Интернет-магазин)

Состав решения (OnlineStore.slnx)

| Проект / файл                       | Назначение в ЛР №4                                                                                                                                     |
| --------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Shared`                                    | Контракты`Request`/`Response`, DTO `Product`, `Order`, `Payment`, новый `DatabaseState`, `IRequestProcessor`                             |
| `Server/Database.cs` **(новое)** | Слой БД: строка подключения,`CREATE DATABASE`, DDL-схема, начальные данные, сброс эксперимента         |
| `Server/BusinessLogic.cs`                   | Операции над БД: чтение, составная операция под**lock**, транзакции COMMIT/ROLLBACK, журнал             |
| `Server/Server.cs`, `RequestHandler.cs`   | Приём нескольких клиентов, параллельная обработка, журнал с идентификатором подключения |
| `Client`                                    | Меню, эксперименты (пункты 5–8), режимы`scenario` и `send` — без единой ссылки на БД                           |
| `Tests`                                     | 32 xUnit-теста против реальной БД (`onlinestore_test`)                                                                                        |
| `docker-compose.yml` **(новое)** | Контейнер PostgreSQL 17                                                                                                                                     |

## Структура базы данных (Требование 3)

DDL выполняется сервером при каждом запуске (идемпотентно, `IF NOT EXISTS`) — листинг из
`Server/Database.cs`, константа `SchemaSql`:

```sql
CREATE TABLE IF NOT EXISTS customers (
    id   SERIAL PRIMARY KEY,
    name TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS products (
    id    SERIAL PRIMARY KEY,
    name  TEXT   NOT NULL,
    price NUMERIC(12,2) NOT NULL CHECK (price >= 0),
    stock INTEGER NOT NULL CHECK (stock >= 0)          -- общий ресурс
);

CREATE TABLE IF NOT EXISTS orders (
    id          SERIAL PRIMARY KEY,
    customer_id INTEGER NOT NULL REFERENCES customers(id),   -- внешний ключ
    status      TEXT NOT NULL DEFAULT 'Created',
    total       NUMERIC(12,2) NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS order_items (
    id         SERIAL PRIMARY KEY,
    order_id   INTEGER NOT NULL REFERENCES orders(id) ON DELETE CASCADE,
    product_id INTEGER NOT NULL REFERENCES products(id),
    quantity   INTEGER NOT NULL CHECK (quantity > 0),
    price      NUMERIC(12,2) NOT NULL
);

CREATE TABLE IF NOT EXISTS payments (
    id         SERIAL PRIMARY KEY,
    order_id   INTEGER NOT NULL REFERENCES orders(id),
    amount     NUMERIC(12,2) NOT NULL CHECK (amount > 0),
    status     TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
```

Проверка в работающей БД  `docker exec onlinestore-pg psql -U store -d onlinestore -p 5434`

## Последовательность сдачи работы

**Шаг 0. Требования:** Docker с Docker Compose, .NET SDK 10, каталог `lab-004`.

**Шаг 1. Запуск СУБД в Docker (Часть 1):**

```bash
cd lab-004
docker compose up -d
docker compose ps
```

Проверка, что БД доступна с хоста:

```bash
docker exec onlinestore-pg psql -U store -d onlinestore -p 5434 \
  -c "SELECT tablename FROM pg_tables WHERE schemaname='public';"
```

**Шаг 2. Запуск сервера (он же создаёт схему и наполняет таблицы):**

```bash
dotnet run --project Server
```

**Шаг 4. Часть 2 — конкурентный доступ двух клиентов:**

```bash
# вариант А: два клиента в одном запуске (два независимых TCP-подключения)
dotnet run --project Client -- scenario race 4 1 100      # lock, товар 4 (ресурс = 1)
dotnet run --project Client -- scenario race-nolock 4 1 100  # без lock — демонстрация гонки

# вариант Б: два отдельных процесса клиента в двух терминалах
dotnet run --project Client -- send Reset
dotnet run --project Client -- send CreateOrder customerId=1 productId=4 quantity=1   # терминал 1
dotnet run --project Client -- send CreateOrder customerId=1 productId=4 quantity=1   # терминал 2 (сразу после)
dotnet run --project Client -- send GetState
```

**Шаг 5. Часть 3 — составная операция и транзакции (4 эксперимента):**

```bash
dotnet run --project Client -- scenario tx 0 0 1 1   # эксперимент 1: без tx, без ошибки
dotnet run --project Client -- scenario tx 0 1 1 1   # эксперимент 2: без tx, ошибка
dotnet run --project Client -- scenario tx 1 0 1 1   # эксперимент 3: tx, без ошибки  → COMMIT
dotnet run --project Client -- scenario tx 1 1 1 1   # эксперимент 4: tx, ошибка      → ROLLBACK
```

Формат: `scenario tx <useTx 0|1> <failAt 0|1|2> <productId> <quantity>`; каждый сценарий сам
сбрасывает данные и печатает состояние таблиц до и после. Заполнить **таблицы 2–4**.

То же самое интерактивно: пункт меню клиента `5 — Эксперимент: составная операция без/с транзакцией`.

**Шаг 6. Часть 4 — тестирование и анализ:**

```bash
dotnet test          # 32 теста, используют отдельную БД onlinestore_test
```

**Шаг 7. Защита** — показать по списку методички: подключение к СУБД, структуру БД,
архитектуру Client → Server → Database, работающие операции, параллельную работу двух клиентов,
общий ресурс, критическую секцию и lock, составную операцию, опыт без транзакции с искусственно
созданной ошибкой и частичным состоянием БД, опыт с COMMIT и ROLLBACK, заполненные таблицы,
unit-тесты и обработку ошибок.

## Часть 2 — Конкурентный доступ и lock

**Общий ресурс:** остаток товара `products.stock` (товар 4, `stock = 1`).
**Критическая секция:** «прочитать ресурс → проверить наличие → записать новое значение» —
всё, что должно быть атомарно относительно других потоков сервера. Проверки, не связанные с
ресурсом (валидация параметров, существование покупателя) и имитация длительной обработки заказа
находятся **вне** lock — под lock не помещается весь метод (Требование 3, Часть 2).

Клиентский запрос поддерживает флаги `useLock`, `useTransaction`, `failAt`
(`Client/RequestBuilder.cs`):

```csharp
public static Request CreateOrder(int customerId, int productId, int quantity,
    bool useTransaction = true, bool useLock = true, int failAt = 0) =>
    new("CreateOrder", new Dictionary<string, string>
    {
        ["customerId"] = customerId.ToString(),
        ["productId"]  = productId.ToString(),
        ["quantity"]   = quantity.ToString(),
        ["useTransaction"] = useTransaction ? "1" : "0",
        ["useLock"]         = useLock ? "1" : "0",
        ["failAt"]          = failAt.ToString()
    });
```

Критическая секция сервера (`Server/BusinessLogic.cs`):

```csharp
log($"[{Timestamp()}] Client {clientId} → START Reserve | Product {productId}, Quantity {quantity}" +
    $" | lock={useLock}, tx={useTransaction}, failAt={failAt}");

if (!useLock)
    return ReserveWithoutLock(...);            // режим «без синхронизации» — для сравнения

Response result;
lock (stockLock)                               // <<< критическая секция на lock
{
    log($"[{Timestamp()}] Client {clientId} → LOCK | вход в критическую секцию");
    result = ReserveCore(connection, ...);     // Check resource → операции с БД
}
log($"[{Timestamp()}] Client {clientId} → UNLOCK | выход из критической секции");
return result;
```

```csharp
private Response ReserveCore(NpgsqlConnection connection, ...)
{
    var product = ReadProduct(connection, productId);           // чтение остатка из БД
    if (product is null) return new(false, "Товар не найден");

    log($"[{Timestamp()}] Client {clientId} → Check resource | Available = {product.Value.Stock}");

    if (product.Value.Stock < quantity)                          // проверка ресурса
        return new(false, "Недостаточно товара на складе");

    return ExecuteCompositeOperation(connection, ...);           // запись: заказ + остаток
}
```

`lock` защищает только код сервера, поэтому в паре с транзакцией получаем два уровня:
синхронизация выполнения кода (нет гонки в приложении) и согласованность изменений в БД
(нет частичных состояний).

### Журнал выполнения сервера (фрагмент, эксперимент с lock

Журнал содержит время, идентификатор подключения клиента, операцию, вход/выход из критической
секции, результат и состояние ресурса. В журнале сервера `Client N` — номер TCP-подключения
(здесь Reset = 1, первый и второй клиенты сценария = 2 и 3); имена Client 1/Client 2 — на стороне клиента.

### Таблица 1 — Результаты конкурентного доступа (2 клиента, Product 4, Resource = 1)

| Механизм                  | Клиент             | Время отправки запроса | Время входа в критическую секцию | Время выхода | Результат                                 |
| --------------------------------- | ------------------------ | ------------------------------------------ | ------------------------------------------------------------ | ----------------------- | -------------------------------------------------- |
| Без синхронизации | Client 1 (подкл. 6) | 21:24:35.349                               | 21:24:35.869 (`Check` без lock)                         | 21:24:36.184            | SUCCESS — товар зарезервирован |
| Без синхронизации | Client 2 (подкл. 7) | 21:24:35.450                               | 21:24:35.955 (`Check` без lock)                         | 21:24:36.264            | SUCCESS — товар зарезервирован |
| lock                              | Client 1 (подкл. 2) | 21:24:24.979                               | 21:24:25.502 (`LOCK`)                                      | 21:24:25.519            | SUCCESS — товар зарезервирован |
| lock                              | Client 2 (подкл. 3) | 21:24:25.080                               | 21:24:25.605 (`LOCK`)                                      | 21:24:25.607            | FAIL — товар отсутствует          |

* **Без синхронизации — некорректный результат:** оба клиента прочитали `Available = 1`, оба
  записали своё значение, оба получили SUCCESS: в БД `orders = 2` при `stock = 1 → 0`
  (lost update, продажа «в минус»). В журнале: `Check resource (без lock) | Available = 1` дважды.
* **С lock — корректный результат:** ровно один SUCCESS и один FAIL, `orders = 1`, `stock = 0`;
  второй клиент вошёл в секцию только после `UNLOCK` первого.
* Результат «без синхронизации» согласуется с экспериментами ЛР №2/ЛР №3.

## Часть 3 — Составная операция и транзакции

**Составная бизнес-операция** (вариант 1): *создание заказа* + *уменьшение остатка* —
минимум два связанных изменения БД: `INSERT INTO orders` (+ `INSERT INTO order_items`)
и `UPDATE products SET stock = ...`. Между операциями может быть искусственно вызвана ошибка
(`failAt = 1` — между операциями, `failAt = 2` — после Операции 2, перед COMMIT).

### Листинг 1 — операция БЕЗ транзакции (автокоммит, `Server/BusinessLogic.cs`)

```csharp
private Response ExecuteWithoutTransaction(NpgsqlConnection connection, int clientId, ...)
{
    var total = price * quantity;
    try
    {
        var orderId = InsertOrder(connection, transaction: null, customerId, total);   // Операция 1
        InsertOrderItem(connection, transaction: null, orderId, productId, quantity, price);
        log($"[{Timestamp()}] Client {clientId} → Operation 1 (CreateOrder) → SUCCESS | OrderId = {orderId}");

        if (failAt == 1)
            throw new SimulatedOperationException("ошибка между Операцией 1 и Операцией 2");

        UpdateStock(connection, transaction: null, productId, newStock);              // Операция 2
        log($"[{Timestamp()}] Client {clientId} → Operation 2 (UpdateStock) → SUCCESS | Available = {newStock}");

        if (failAt == 2)
            throw new SimulatedOperationException("ошибка после Операции 2 (перед COMMIT)");

        return new(true, "Заказ создан", BuildOrder(...), "OrderCreated");
    }
    catch (SimulatedOperationException exception)
    {
        log($"[{Timestamp()}] Client {clientId} → ERROR | {exception.Message}");
        log($"[{Timestamp()}] Client {clientId} → Без транзакции откат невозможен — состояние БД ЧАСТИЧНО ИЗМЕНЕНО");
        return new(false, $"Искусственная ошибка: {exception.Message}. Транзакция не использовалась — " +
                          "частичные изменения сохранены в БД");
    }
    catch (NpgsqlException exception) { /* ошибка БД: изменения уже частично сохранены */ }
}
```

### Листинг 2 — та же операция С транзакцией (BEGIN → COMMIT / ROLLBACK)

```csharp
private Response ExecuteWithTransaction(NpgsqlConnection connection, int clientId, ...)
{
    using var transaction = connection.BeginTransaction();                             // BEGIN
    try
    {
        var orderId = InsertOrder(connection, transaction, customerId, total);         // Операция 1
        InsertOrderItem(connection, transaction, orderId, productId, quantity, price);
        log($"... → BEGIN | Operation 1 (CreateOrder) → SUCCESS | OrderId = {orderId}");

        if (failAt == 1)
            throw new SimulatedOperationException("ошибка между Операцией 1 и Операцией 2");

        UpdateStock(connection, transaction, productId, newStock);                    // Операция 2
        log($"... → Operation 2 (UpdateStock) → SUCCESS | Available = {newStock}");

        if (failAt == 2)
            throw new SimulatedOperationException("ошибка после Операции 2 (перед COMMIT)");

        transaction.Commit();                                                          // COMMIT
        log($"... → COMMIT | изменения подтверждены | Available = {newStock}");
        return new(true, "Заказ создан, транзакция подтверждена (COMMIT)", BuildOrder(...), "OrderCreated");
    }
    catch (SimulatedOperationException exception)
    {
        transaction.Rollback();                                                        // ROLLBACK
        log($"... → ERROR | {exception.Message}");
        log($"... → ROLLBACK | изменения отменены — состояние БД как до BEGIN");
        return new(false, $"Искусственная ошибка: {exception.Message}. Выполнен ROLLBACK — изменения отменены");
    }
    catch (NpgsqlException exception)
    {
        var rollbackMessage = TryRollback(transaction);   // невозможность COMMIT → гарантированный ROLLBACK
        return new(false, $"Ошибка БД внутри транзакции: {exception.Message}. {rollbackMessage}");
    }
}
```

Каждый `NpgsqlCommand` получает `command.Transaction = transaction`, поэтому все операции входят
в одну транзакцию. Ошибки (`SimulatedOperationException`, `NpgsqlException`) перехватываются,
`ROLLBACK` выполняется до того, как исключение дойдёт до обработчика запроса.

## Часть 4 — Тестирование и обработка ошибок

### Unit-тесты (Требование 7)

```bash
dotnet test          # Пройдено: 32, не пройдено: 0 (около 8 с)
```

Тесты выполняются против **реальной** базы: `DbFixture` создаёт отдельную БД
`onlinestore_test` на том же сервере PostgreSQL (переопределяется `ONLINESTORE_TEST_DB`),
схема создаётся идемпотентно, перед каждым тестом данные сбрасываются; тесты идут
последовательно (`xunit.runner.json`: `parallelizeTestCollections: false`).

| Набор (файл)       | Кол-во | Что подтверждает (по методичке)                                                                                                                                                                                                                                                                                  |
| --------------------------- | ----------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `ResourceOperationTests`  | 4           | операции чтения/изменения через сервер в БД, связи по внешним ключам,`GetState`, `Reset`                                                                                                                                                                                      |
| `LockConcurrencyTests`    | 2           | корректность операции с общим ресурсом;**невозможность выдать ресурс двум клиентам** (lock); демонстрация гонки без lock                                                                                                                  |
| `TransactionTests`        | 7           | успешное выполнение составной операции;**изменения сохраняются после COMMIT**; **нет частичных изменений после ROLLBACK**; 4 эксперимента + ошибка перед COMMIT + возврат товара при отмене |
| `ErrorHandlingTests`      | 9           | сервер недоступен, БД недоступна, некорректный/неизвестный запрос, отсутствующий ресурс/запись, занятый ресурс, нарушение`CHECK`, отключение клиента                                                            |
| `ParallelProcessingTests` | 3           | параллельная обработка нескольких клиентов                                                                                                                                                                                                                                                          |
| `ServerAvailabilityTests` | 2           | сервер доступен и отдаёт состояние БД                                                                                                                                                                                                                                                                      |
| `TimeSyncTests`           | 5           | синхронизация времени (сохранена из ЛР №3)                                                                                                                                                                                                                                                               |

Ключевой тест невозможности двойной выдачи (`Tests/LockConcurrencyTests.cs`):

```csharp
[Fact]
public async Task TwoClients_LastItem_WithLock_ExactlyOneSucceeds()
{
    DbFixture.Reset();
    await using var server = new TestServer();

    var responses = await Task.WhenAll(
        TestServer.SendAsync(server.Port, OrderRequest(useLock: true)),
        TestServer.SendAsync(server.Port, OrderRequest(useLock: true)));

    Assert.Single(responses, response => response.Success);   // ровно один SUCCESS
    Assert.Single(responses, response => !response.Success);  // и один FAIL
    Assert.Equal(0, DbFixture.Stock(4));                      // ресурс выдан один раз
    Assert.Contains(server.Logs, line => line.Contains("→ LOCK"));
}
```

Проверка отсутствия частичных изменений после ROLLBACK (`Tests/TransactionTests.cs`):

```csharp
var response = await TestServer.SendAsync(server.Port, OrderRequest(useTransaction: true, failAt: 1));

Assert.False(response.Success);
Assert.Contains("ROLLBACK", response.Message);
Assert.Equal(0, DbFixture.Count("SELECT COUNT(*) FROM orders"));  // Операция 1 отменена
Assert.Equal(5, DbFixture.Stock(1));                              // Операция 2 не выполнялась
Assert.Contains(server.Logs, line => line.Contains("→ ROLLBACK"));
```

### Обработка ошибок (Требование 8)

| Ситуация                                                                          | Реализация                                                                                                                                                                                                           |
| ----------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Сервер недоступен                                                         | клиент:`SocketException → «Сервер недоступен»`, приложение не падает                                                                                                              |
| Соединение с БД отсутствует / ошибка подключения | `NpgsqlException` перехватывается в `HandleAsync` → `Response(false, «Ошибка базы данных: …»)`; на старте сервер пишет причину и рекомендацию |
| Некорректный запрос / пустая операция                     | `«Некорректный запрос»` (проверка до обработки)                                                                                                                                       |
| Неизвестная операция                                                   | ветка`default` switch → `«Неизвестная операция»`                                                                                                                                                |
| Отсутствующий ресурс / запись                                    | `«Товар не найден»`, `«Заказ не найден»`, `«Покупатель не найден»`                                                                                                     |
| Занятый ресурс                                                               | `«Недостаточно товара на складе»` — остаток не меняется                                                                                                                        |
| Нарушение ограничения БД                                            | `CHECK`-ограничения в DDL + тест `CheckConstraint_RejectsInvalidData`                                                                                                                                      |
| Ошибка внутри транзакции / невозможность COMMIT        | `catch (NpgsqlException)` → `TryRollback()` с журналом → клиенту «… Выполнен ROLLBACK»                                                                                                        |
| Отключение клиента во время обработки                    | `RequestHandler` логирует «ОТКЛЮЧЁН», сервер продолжает работу (тест)                                                                                                            |

Приложение не завершается аварийно на ожидаемых ошибках: каждый запрос возвращает
`Response(false, …)` с описанием, сервер продолжает обслуживать других клиентов.
