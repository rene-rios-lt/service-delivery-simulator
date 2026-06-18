using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

public interface IBackendApiClient
{
    Task PostPositionAsync(VehiclePosition position, CancellationToken cancellationToken);
    Task AcceptJobOfferAsync(string offerId, RepIdentity rep, CancellationToken cancellationToken);
    Task DeclineJobOfferAsync(string offerId, RepIdentity rep, CancellationToken cancellationToken);

    // SIM-006: the automated rep marks "I've Arrived" when its truck reaches the
    // requester — POST /rep/arrive with the rep's bearer token. Fired only from the
    // gated auto-decision path so it never fires for a human-controlled rep.
    Task ArriveAsync(RepIdentity rep, CancellationToken cancellationToken);

    // SIM-008: the single authoritative fleet-state read (Simulator token) plus the
    // rep-token claim operations used at startup and during rebalance.
    Task<IReadOnlyList<FleetStateRow>> GetFleetStateAsync(CancellationToken cancellationToken);
    Task ClaimVehicleAsync(string vehicleId, RepIdentity rep, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetAvailableVehicleIdsAsync(RepIdentity rep, CancellationToken cancellationToken);
}
