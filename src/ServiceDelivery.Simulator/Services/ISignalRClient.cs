using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

public interface ISignalRClient : IAsyncDisposable
{
    void RegisterJobOfferHandler(Action<JobOfferPayload> handler);
    Task ConnectAsync(string jwt, CancellationToken cancellationToken);
}
