using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

public sealed class FleetClaimCoordinator : IFleetClaimCoordinator
{
    private readonly IIdentitySessionStore _sessionStore;
    private readonly IBackendApiClient _apiClient;
    private readonly IYieldedRepRegistry _yieldedReps;
    private readonly ILogger<FleetClaimCoordinator> _logger;

    // BUG-052: vehicles this simulator run has already claimed. GET /vehicles/available
    // is the human take-over listing — it includes claimed-but-idle vehicles — so
    // without a local memory each rep re-selects the same front-of-list vehicle every
    // tick (stampede/livelock, starving vehicles 5–8). Sticky for the run, mirroring
    // YieldedRepRegistry; ConcurrentDictionary keeps it consistent with the tick thread.
    private readonly ConcurrentDictionary<string, byte> _locallyClaimedVehicleIds = new();

    public FleetClaimCoordinator(
        IIdentitySessionStore sessionStore,
        IBackendApiClient apiClient,
        IYieldedRepRegistry yieldedReps,
        ILogger<FleetClaimCoordinator> logger)
    {
        _sessionStore = sessionStore;
        _apiClient = apiClient;
        _yieldedReps = yieldedReps;
        _logger = logger;
    }

    public async Task ClaimInitialVehiclesAsync(CancellationToken cancellationToken)
    {
        var reps = _sessionStore.Reps;
        for (int i = 0; i < reps.Count; i++)
            await ClaimOneFreeVehicleAsync(reps[i], i, cancellationToken);
    }

    public async Task RebalanceAsync(IReadOnlyList<FleetStateRow> snapshot, CancellationToken cancellationToken)
    {
        var repsHoldingVehicles = snapshot
            .Where(row => row.ClaimingRepId is not null)
            .Select(row => row.ClaimingRepId!)
            .ToHashSet();

        var reps = _sessionStore.Reps;
        for (int i = 0; i < reps.Count; i++)
        {
            var rep = reps[i];

            // SIM-009 AC-4: a rep yielded to a human earlier in the run is never
            // re-assumed, so it is never given a claim again for the rest of the run.
            if (_yieldedReps.IsRepYielded(rep.RepId))
                continue;

            if (rep.RepId is not null && repsHoldingVehicles.Contains(rep.RepId))
                continue;

            await ClaimOneFreeVehicleAsync(rep, i, cancellationToken);
        }
    }

    private async Task ClaimOneFreeVehicleAsync(RepIdentity rep, int repIndex, CancellationToken cancellationToken)
    {
        var available = await _apiClient.GetAvailableVehicleIdsAsync(rep, cancellationToken);

        // SIM-009 AC-4: a vehicle yielded with a human-controlled rep ("gone home for
        // the night") is never re-claimed by anyone for the rest of the run.
        var candidates = available
            .Where(id => !_yieldedReps.IsVehicleYielded(id) && !_locallyClaimedVehicleIds.ContainsKey(id))
            .ToList();

        if (candidates.Count == 0)
        {
            _logger.LogWarning("No free vehicle available to claim for rep {RepId}.", rep.RepId);
            return;
        }

        // BUG-052: fan reps out across the (identical) take-over listing by starting
        // each rep at a distinct offset, so concurrent reps do not all target the
        // front-of-list vehicle; on a 409 conflict advance to the next candidate
        // (wrapping around) rather than re-selecting the same taken vehicle.
        var startIndex = repIndex % candidates.Count;
        for (int offset = 0; offset < candidates.Count; offset++)
        {
            var vehicleId = candidates[(startIndex + offset) % candidates.Count];
            var outcome = await _apiClient.ClaimVehicleAsync(vehicleId, rep, cancellationToken);

            // BUG-052 (binding constraint): record the vehicle as locally-claimed on a
            // successful claim OR a 409 Conflict (both mean "not available to me") so no
            // other rep re-selects it. Do NOT record a transient Failure (5xx/network) —
            // that vehicle may still be free, and permanently excluding it would starve
            // a genuinely-free vehicle for the rest of the run.
            if (outcome != ClaimOutcome.Failed)
                _locallyClaimedVehicleIds.TryAdd(vehicleId, 0);

            if (outcome == ClaimOutcome.Claimed)
                return;
        }

        _logger.LogWarning(
            "Rep {RepId} could not claim any of {Count} candidate vehicles.", rep.RepId, candidates.Count);
    }
}
