using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

public interface ISignalRClient : IAsyncDisposable
{
    void RegisterJobOfferHandler(Action<string, JobOfferPayload> handler);
    Task ConnectAllAsync(IEnumerable<RepIdentity> reps, CancellationToken cancellationToken);
}
