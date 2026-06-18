using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

// SIM-006: fires the single arrive call for an operated rep whose truck has reached the
// requester. It is the handoff point to SIM-010 (dwell + complete). One responsibility:
// detect first-arrival for an automated rep and post arrive once. The human/automated
// split rides on the existing RepOperationGate so this NEVER fires for a human rep, and
// it caches no destination — "reached" is recomputed each call from the shared navigator
// predicate against the row's current ActiveRequestLocation, so redirects are honoured.
public sealed class ArrivalReporter : IArrivalReporter
{
    private readonly IBackendApiClient _apiClient;
    private readonly IStraightLineNavigator _navigator;
    private readonly IVehiclePositionProvider _positionProvider;
    private readonly IRepOperationGate _operationGate;
    private readonly IIdentitySessionStore _sessionStore;
    private readonly ILogger<ArrivalReporter> _logger;

    // Vehicles for which arrive has already been posted this arrival. Concurrent because
    // the reconciler may drive ticks while offers are handled on the SignalR thread.
    private readonly ConcurrentDictionary<string, byte> _arrivedVehicleIds = new();

    public ArrivalReporter(
        IBackendApiClient apiClient,
        IStraightLineNavigator navigator,
        IVehiclePositionProvider positionProvider,
        IRepOperationGate operationGate,
        IIdentitySessionStore sessionStore,
        ILogger<ArrivalReporter> logger)
    {
        _apiClient = apiClient;
        _navigator = navigator;
        _positionProvider = positionProvider;
        _operationGate = operationGate;
        _sessionStore = sessionStore;
        _logger = logger;
    }

    public async Task ReportArrivalIfReachedAsync(FleetStateRow row, CancellationToken cancellationToken)
    {
        if (row.ActiveRequestLocation is not { } target)
        {
            _arrivedVehicleIds.TryRemove(row.VehicleId, out _);
            return;
        }

        if (!_operationGate.ShouldOperate(row))
            return;

        if (row.RepState is not (RepState.EnRoute or RepState.Within15Miles))
            return;

        if (!_positionProvider.TryGetPosition(row.VehicleId, out var position))
            return;

        if (!_navigator.HasReached(position.Lat, position.Lng, target.Lat, target.Lng))
            return;

        if (!_arrivedVehicleIds.TryAdd(row.VehicleId, 0))
            return;

        var identity = _sessionStore.Reps.FirstOrDefault(r => r.RepId == row.ClaimingRepId);
        if (identity is null)
        {
            _arrivedVehicleIds.TryRemove(row.VehicleId, out _);
            _logger.LogWarning(
                "Truck {VehicleId} reached the requester but no operated identity matches rep {RepId}; cannot arrive.",
                row.VehicleId, row.ClaimingRepId);
            return;
        }

        await _apiClient.ArriveAsync(identity, cancellationToken);
    }
}
