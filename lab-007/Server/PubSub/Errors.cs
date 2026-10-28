namespace OnlineStore.Server;

public sealed class BrokerUnavailableException(string message = "Broker недоступен") : Exception(message);

public sealed class SubscriberUnavailableException(string message) : Exception(message);
