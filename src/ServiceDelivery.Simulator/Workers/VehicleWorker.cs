using ServiceDelivery.Simulator.Services;

namespace ServiceDelivery.Simulator.Workers;

// One instance per vehicle. On each 3-second tick:
// - Advances vehicle position along its pre-determined Iowa route waypoints
// - Posts the updated position to the backend API
// - Checks for an active job offer and auto-accepts or declines
// - If assigned a job, deviates from the loop route toward the requester location
public sealed class VehicleWorker : BackgroundService
{
    private readonly int _vehicleIndex;
    private readonly IBackendApiClient _apiClient;
    private readonly ILogger<VehicleWorker> _logger;

    public VehicleWorker(int vehicleIndex, IBackendApiClient apiClient, ILogger<VehicleWorker> logger)
    {
        _vehicleIndex = vehicleIndex;
        _apiClient = apiClient;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("VehicleWorker {VehicleIndex} starting", _vehicleIndex);

        while (!stoppingToken.IsCancellationRequested)
        {
            // TODO: Advance position along route waypoints
            // TODO: Post position update to backend
            // TODO: Check for pending job offer and accept/decline
            // TODO: If active job, navigate toward requester location instead of looping

            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
    }
}
