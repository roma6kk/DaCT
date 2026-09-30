using OnlineStore.Server.SemaphoreSlimSync;

// Требование 9 (Часть 1): сервер на SemaphoreSlim.
// Выбор реализации — запуском соответствующего сервера (Server или Server.SemaphoreSlim).
await new OnlineStore.Server.Server(
    requestProcessor: new SemaphoreSlimBusinessLogic(log: Console.WriteLine)
).RunAsync(CancellationToken.None);