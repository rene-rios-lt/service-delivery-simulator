using Microsoft.Extensions.Logging;
using ServiceDelivery.Simulator.Models;
using ServiceDelivery.Simulator.Services;

namespace ServiceDelivery.Simulator.Workers;

// One instance per vehicle. Under SIM-008 topology A this is a plain per-vehicle
// drive object (no longer a BackgroundService): the FleetReconciler owns the tick and
// calls DriveAsync once per tick with the vehicle's fleet-state row and resolved drive
// mode. The IdleLoop branch advances the vehicle along its Iowa loop and posts the
// position (the SIM-004 behaviour). Navigate/Hold geometry is owned by SIM-006 — this
// story dispatches the mode and leaves those branches as seams.
public sealed class VehicleWorker
{
    private readonly VehicleRoute _route;
    private readonly IBackendApiClient _apiClient;
    private readonly ILogger<VehicleWorker> _logger;

    private int _waypointIndex;

    public VehicleWorker(VehicleRoute route, IBackendApiClient apiClient, ILogger<VehicleWorker> logger)
    {
        _route = route;
        _apiClient = apiClient;
        _logger = logger;
    }

    public string VehicleId => _route.VehicleId;

    public async Task DriveAsync(FleetStateRow row, VehicleDriveMode mode, CancellationToken cancellationToken)
    {
        switch (mode)
        {
            case VehicleDriveMode.IdleLoop:
                await PostLoopPositionAsync(cancellationToken);
                break;

            // Navigate/Hold geometry is owned by SIM-006. This story dispatches the
            // mode from fleet-state and leaves these branches for that story.
            case VehicleDriveMode.Navigate:
            case VehicleDriveMode.Hold:
            default:
                break;
        }
    }

    private async Task PostLoopPositionAsync(CancellationToken cancellationToken)
    {
        _waypointIndex = (_waypointIndex + 1) % _route.Waypoints.Count;
        var waypoint = _route.Waypoints[_waypointIndex];
        var position = new VehiclePosition(_route.VehicleId, waypoint.Latitude, waypoint.Longitude);

        try
        {
            await _apiClient.PostPositionAsync(position, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to post position for vehicle {VehicleId}. Will retry on next tick.", _route.VehicleId);
        }
    }
}
