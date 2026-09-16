# Лабораторная работа №1

Вариант 1: интернет-магазин.

## Структура

- `Client` — клиент и формирование запросов.
- `Server` — TCP-сервер, обработчик запросов и бизнес-логика.
- `Shared` — сущности и JSON-протокол.
- `Tests` — unit-тесты бизнес-логики.

## Запуск

В первом терминале:

```bash
dotnet run --project Server/OnlineStore.Server.csproj
```

Во втором терминале:

```bash
dotnet run --project Client/OnlineStore.Client.csproj
```

Тесты:

```bash
dotnet test
```

## Протокол

Клиент отправляет серверу одну JSON-строку с полями `Operation` и `Data`. Сервер возвращает JSON-ответ с полями `Success`, `Message`, `Data` и `Event`.

Поддерживаются операции `GetProducts`, `CreateOrder`, `CancelOrder`, `PayOrder`.
