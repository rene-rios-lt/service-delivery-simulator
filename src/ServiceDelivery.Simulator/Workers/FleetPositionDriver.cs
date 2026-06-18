using Microsoft.Extensions.Logging;
using ServiceDelivery.Simulator.Models;
using ServiceDelivery.Simulator.Services;

namespace ServiceDelivery.Simulator.Workers;

// Fleet-wide position driver: holds one VehicleWorker per vehicle and routes each
// per-tick drive call to the worker for that vehicle id. This keeps the per-vehicle
// loop state (waypoint index) on the worker while giving the FleetReconciler a single
// IVehiclePositionDriver to depend on.
public sealed class FleetPositionDriver : IVehiclePositionDriver, IVehiclePositionProvider
{
    private readonly IReadOnlyDictionary<string, VehicleWorker> _workersByVehicleId;
    private readonly ILogger<FleetPositionDriver> _logger;

    public FleetPositionDriver(IEnumerable<VehicleWorker> workers, ILogger<FleetPositionDriver> logger)
    {
        _workersByVehicleId = workers.ToDictionary(worker => worker.VehicleId);
        _logger = logger;
    }

    public Task DriveAsync(FleetStateRow row, VehicleDriveMode mode, CancellationToken cancellationToken)
    {
        if (_workersByVehicleId.TryGetValue(row.VehicleId, out var worker))
            return worker.DriveAsync(row, mode, cancellationToken);

        _logger.LogWarning("No VehicleWorker registered for vehicle {VehicleId}; skipping drive.", row.VehicleId);
        return Task.CompletedTask;
    }

    public bool TryGetPosition(string vehicleId, out (double Lat, double Lng) position)
    {
        if (_workersByVehicleId.TryGetValue(vehicleId, out var worker))
            return worker.TryGetCurrentPosition(out position);

        position = default;
        return false;
    }
}
