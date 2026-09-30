using OnlineStore.Server;

await new Server(timeResponseDelay: TimeSpan.FromMilliseconds(200))
    .RunAsync(CancellationToken.None);