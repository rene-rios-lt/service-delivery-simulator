using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

// Owns assignment of vehicles to the automated reps: the startup claim (one free
// vehicle per rep) and per-tick rebalance (claim a free vehicle for any operated rep
// that currently holds none).
public interface IFleetClaimCoordinator
{
    Task ClaimInitialVehiclesAsync(CancellationToken cancellationToken);
    Task RebalanceAsync(IReadOnlyList<FleetStateRow> snapshot, CancellationToken cancellationToken);
}
