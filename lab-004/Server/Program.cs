using OnlineStore.Server;


var connectionString = Db.ConnectionString;
Console.WriteLine($"База данных: {connectionString.Replace("Password=store", "Password=***")}");


try
{
    Db.EnsureDatabase(connectionString, Console.WriteLine);
    Db.EnsureSchema(connectionString, Console.WriteLine);
}
catch (Exception exception)
{
    Console.WriteLine($"[DB] База данных недоступна: {exception.Message}");
    Console.WriteLine("[DB] Запустите PostgreSQL (docker compose up -d) и перезапустите сервер.");
    Console.WriteLine("[DB] Сервер будет остановлен — ожидаемая ошибка подключения, не аварийное завершение.");
    return;
}

await new Server(connectionString: connectionString, timeResponseDelay: TimeSpan.FromMilliseconds(200))
    .RunAsync(CancellationToken.None);
