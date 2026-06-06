namespace ServiceDelivery.Simulator.Services;

public interface ISignalRClient : IAsyncDisposable
{
    Task ConnectAsync(string jwt, CancellationToken cancellationToken);
}
