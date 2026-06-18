using Microsoft.Extensions.Logging;
using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

public sealed class FleetClaimCoordinator : IFleetClaimCoordinator
{
    private readonly IIdentitySessionStore _sessionStore;
    private readonly IBackendApiClient _apiClient;
    private readonly ILogger<FleetClaimCoordinator> _logger;

    public FleetClaimCoordinator(
        IIdentitySessionStore sessionStore,
        IBackendApiClient apiClient,
        ILogger<FleetClaimCoordinator> logger)
    {
        _sessionStore = sessionStore;
        _apiClient = apiClient;
        _logger = logger;
    }

    public async Task ClaimInitialVehiclesAsync(CancellationToken cancellationToken)
    {
        foreach (var rep in _sessionStore.Reps)
            await ClaimOneFreeVehicleAsync(rep, cancellationToken);
    }

    public async Task RebalanceAsync(IReadOnlyList<FleetStateRow> snapshot, CancellationToken cancellationToken)
    {
        var repsHoldingVehicles = snapshot
            .Where(row => row.ClaimingRepId is not null)
            .Select(row => row.ClaimingRepId!)
            .ToHashSet();

        foreach (var rep in _sessionStore.Reps)
        {
            if (rep.RepId is not null && repsHoldingVehicles.Contains(rep.RepId))
                continue;

            await ClaimOneFreeVehicleAsync(rep, cancellationToken);
        }
    }

    private async Task ClaimOneFreeVehicleAsync(RepIdentity rep, CancellationToken cancellationToken)
    {
        var available = await _apiClient.GetAvailableVehicleIdsAsync(rep, cancellationToken);
        var vehicleId = available.FirstOrDefault();
        if (vehicleId is null)
        {
            _logger.LogWarning("No free vehicle available to claim for rep {RepId}.", rep.RepId);
            return;
        }

        await _apiClient.ClaimVehicleAsync(vehicleId, rep, cancellationToken);
    }
}
