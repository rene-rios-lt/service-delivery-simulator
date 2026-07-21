using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

public interface ISignalRClient : IAsyncDisposable
{
    void RegisterJobOfferHandler(Action<string, JobOfferPayload> handler);
    Task ConnectAllAsync(IEnumerable<RepIdentity> reps, CancellationToken cancellationToken);

    // Called once per operated rep per reconciler tick. If that rep's connection is not
    // Connected, attempt a restart so a deaf connection heals within one tick. A no-op
    // when the rep was never connected or is already healthy; never throws.
    Task EnsureConnectedAsync(string repId, CancellationToken cancellationToken);
}
