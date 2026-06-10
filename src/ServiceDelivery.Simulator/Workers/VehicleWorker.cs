using ServiceDelivery.Simulator.Models;
using ServiceDelivery.Simulator.Services;

namespace ServiceDelivery.Simulator.Workers;

// One instance per vehicle. On each 3-second tick:
// - Advances vehicle position along its pre-determined Iowa route waypoints
// - Posts the updated position to the backend API
// - Checks for an active job offer and auto-accepts or declines
// - If assigned a job, deviates from the loop route toward the requester location
public sealed class VehicleWorker : BackgroundService
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

    public async Task TickAsync(CancellationToken cancellationToken)
    {
        _waypointIndex = (_waypointIndex + 1) % _route.Waypoints.Count;
        var waypoint = _route.Waypoints[_waypointIndex];
        var position = new VehiclePosition(_route.VehicleId, waypoint.Latitude, waypoint.Longitude);
        await _apiClient.PostPositionAsync(position, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("VehicleWorker {VehicleId} starting", _route.VehicleId);

        while (!stoppingToken.IsCancellationRequested)
        {
            await TickAsync(stoppingToken);

            // TODO: Check for pending job offer and accept/decline
            // TODO: If active job, navigate toward requester location instead of looping

            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
    }
}
