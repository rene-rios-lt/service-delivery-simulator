using Microsoft.Extensions.Logging;
using ServiceDelivery.Simulator.Models;
using ServiceDelivery.Simulator.Services;

namespace ServiceDelivery.Simulator.Workers;

// One instance per vehicle. Under SIM-008 topology A this is a plain per-vehicle
// drive object (no longer a BackgroundService): the FleetReconciler owns the tick and
// calls DriveAsync once per tick with the vehicle's fleet-state row and resolved drive
// mode. The IdleLoop branch advances the vehicle along its Iowa loop and posts the
// position (the SIM-004 behaviour). Navigate/Hold geometry is owned by SIM-006.
//
// SIM-006: the worker drives EVERY truck — including human-controlled ones — and only
// ever posts position. It NEVER makes a rep action call; the arrive call lives in the
// gated auto-decision path (ArrivalReporter). The worker caches NO destination: each
// Navigate tick reads the target fresh from row.ActiveRequestLocation, so redirects
// re-navigate automatically.
public sealed class VehicleWorker
{
    private readonly VehicleRoute _route;
    private readonly IBackendApiClient _apiClient;
    private readonly IStraightLineNavigator _navigator;
    private readonly ILogger<VehicleWorker> _logger;

    private int _waypointIndex;

    // The last position this worker posted. Navigate steps from here; null until the
    // first post, in which case the current loop waypoint is the starting point.
    private double? _lastLat;
    private double? _lastLng;

    // SIM-007: true when the previous drive tick was a non-loop mode (Navigate/Hold),
    // i.e. the vehicle was on a job excursion. Consumed by the IdleLoop branch so the
    // first Available loop tick after an excursion reattaches to the nearest waypoint
    // instead of blindly advancing the stale loop index.
    private bool _wasOnExcursionLastTick;

    public VehicleWorker(
        VehicleRoute route,
        IBackendApiClient apiClient,
        IStraightLineNavigator navigator,
        ILogger<VehicleWorker> logger)
    {
        _route = route;
        _apiClient = apiClient;
        _navigator = navigator;
        _logger = logger;
    }

    public string VehicleId => _route.VehicleId;

    public bool TryGetCurrentPosition(out (double Lat, double Lng) position)
    {
        if (_lastLat is { } lat && _lastLng is { } lng)
        {
            position = (lat, lng);
            return true;
        }

        position = default;
        return false;
    }

    public async Task DriveAsync(FleetStateRow row, VehicleDriveMode mode, CancellationToken cancellationToken)
    {
        switch (mode)
        {
            case VehicleDriveMode.IdleLoop:
                await PostLoopPositionAsync(row, cancellationToken);
                break;

            case VehicleDriveMode.Navigate:
                await PostNavigateStepAsync(row, cancellationToken);
                _wasOnExcursionLastTick = true;
                break;

            case VehicleDriveMode.Hold:
                await PostHoldPositionAsync(row, cancellationToken);
                _wasOnExcursionLastTick = true;
                break;

            default:
                break;
        }
    }

    private async Task PostLoopPositionAsync(FleetStateRow row, CancellationToken cancellationToken)
    {
        // AC-4: VehicleDriveResolver maps an Offline (parked / off-duty) row to IdleLoop,
        // so mode alone cannot tell "job done → resume loop" apart from "human off-duty →
        // park". Gate all loop action on Available: for a parked row take no action — no
        // reattach, no index advance, no moved position — leaving parking to SIM-009.
        if (row.RepState != RepState.Available)
            return;

        // AC-1/AC-2/AC-3: first loop tick after a Navigate/Hold excursion reattaches to
        // the Haversine-nearest waypoint and clears cached job-nav state, rather than
        // blindly advancing the stale loop index.
        if (_wasOnExcursionLastTick)
        {
            var (currentLat, currentLng) = CurrentPosition();
            _waypointIndex = _navigator.NearestWaypointIndex(currentLat, currentLng, _route.Waypoints);
            _wasOnExcursionLastTick = false;
            _lastLat = null;
            _lastLng = null;
        }
        else
        {
            _waypointIndex = (_waypointIndex + 1) % _route.Waypoints.Count;
        }

        var waypoint = _route.Waypoints[_waypointIndex];
        await PostPositionAsync(waypoint.Latitude, waypoint.Longitude, cancellationToken);
    }

    private async Task PostNavigateStepAsync(FleetStateRow row, CancellationToken cancellationToken)
    {
        if (row.ActiveRequestLocation is null)
            return;

        var (currentLat, currentLng) = CurrentPosition();
        var step = _navigator.Step(
            currentLat, currentLng,
            row.ActiveRequestLocation.Lat, row.ActiveRequestLocation.Lng);

        await PostPositionAsync(step.Lat, step.Lng, cancellationToken);
    }

    private async Task PostHoldPositionAsync(FleetStateRow row, CancellationToken cancellationToken)
    {
        var (holdLat, holdLng) = row.ActiveRequestLocation is { } target
            ? (target.Lat, target.Lng)
            : CurrentPosition();

        await PostPositionAsync(holdLat, holdLng, cancellationToken);
    }

    private (double Lat, double Lng) CurrentPosition() =>
        _lastLat is { } lat && _lastLng is { } lng
            ? (lat, lng)
            : (_route.Waypoints[_waypointIndex].Latitude, _route.Waypoints[_waypointIndex].Longitude);

    private async Task PostPositionAsync(double lat, double lng, CancellationToken cancellationToken)
    {
        var position = new VehiclePosition(_route.VehicleId, lat, lng);

        try
        {
            await _apiClient.PostPositionAsync(position, cancellationToken);
            _lastLat = lat;
            _lastLng = lng;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to post position for vehicle {VehicleId}. Will retry on next tick.", _route.VehicleId);
        }
    }
}
