using OnlineStore.Server;


var primaryConnectionString = Db.ConnectionString;
var replicaConnectionString = Db.ReplicaConnectionString;
Console.WriteLine($"Primary: {primaryConnectionString.Replace("Password=store", "Password=***")}");
Console.WriteLine($"Replica: {replicaConnectionString.Replace("Password=store", "Password=***")}");

var instances = new InstanceManager(primaryConnectionString, replicaConnectionString, Console.WriteLine);


var initialized = 0;
foreach (var instance in new[] { instances.Primary, instances.Replica! })
{
    try
    {
        Db.EnsureDatabase(instance.ConnectionString, Console.WriteLine);
        Db.EnsureSchema(instance.ConnectionString, Console.WriteLine);
        initialized++;
    }
    catch (Exception exception)
    {
        Console.WriteLine($"[DB] {instance.Name} недоступен при старте: {exception.Message}");
    }
}

if (initialized == 0)
{
    Console.WriteLine("[DB] Ни один экземпляр не доступен. Запустите PostgreSQL (docker compose up -d) и перезапустите сервер.");
    return;
}

Console.WriteLine("[DB] Начальное состояние: выполните операцию Reset (пункт 7 клиента), чтобы Primary и Replica совпали.");

var logic = new BusinessLogic(instances, timeResponseDelay: TimeSpan.FromMilliseconds(200), log: Console.WriteLine);

var pubSub = new PubSubSystem(Console.WriteLine);
pubSub.Start();
Console.WriteLine("[PubSub] Broker запущен, подписчики: Logger, StockService, NotificationService");

var processor = new PubSubRequestProcessor(logic, pubSub, Console.WriteLine);
await new Server(requestProcessor: processor, log: Console.WriteLine)
    .RunAsync(CancellationToken.None);
