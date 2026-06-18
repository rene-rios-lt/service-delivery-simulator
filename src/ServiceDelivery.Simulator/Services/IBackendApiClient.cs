using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

public interface IBackendApiClient
{
    Task PostPositionAsync(VehiclePosition position, CancellationToken cancellationToken);
    Task AcceptJobOfferAsync(string offerId, RepIdentity rep, CancellationToken cancellationToken);
    Task DeclineJobOfferAsync(string offerId, RepIdentity rep, CancellationToken cancellationToken);

    // SIM-008: the single authoritative fleet-state read (Simulator token) plus the
    // rep-token claim operations used at startup and during rebalance.
    Task<IReadOnlyList<FleetStateRow>> GetFleetStateAsync(CancellationToken cancellationToken);
    Task ClaimVehicleAsync(string vehicleId, RepIdentity rep, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetAvailableVehicleIdsAsync(RepIdentity rep, CancellationToken cancellationToken);
}
