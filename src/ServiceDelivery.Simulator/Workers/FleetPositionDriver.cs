using Microsoft.Extensions.Logging;
using ServiceDelivery.Simulator.Models;
using ServiceDelivery.Simulator.Services;

namespace ServiceDelivery.Simulator.Workers;

// Fleet-wide position driver: holds the injected VehicleWorkers as an unordered POOL and
// assigns one worker to each backend vehicle GUID lazily, in fleet-state arrival order,
// sticky for the process run (a given GUID always keeps the same worker).
//
// BUG-017: the workers were previously keyed by worker.VehicleId — the route's cosmetic
// REGISTRATION label (e.g. "V-1"). But every FleetStateRow.VehicleId is the backend GUID
// the system actually keys vehicles by, so DriveAsync and TryGetPosition never matched a
// worker, silently skipped the drive, and no position was ever posted. The fix decouples
// route geometry (the worker's registration) from vehicle identity (the GUID): both the
// drive path and the provider path now resolve through one GUID→worker map.
public sealed class FleetPositionDriver : IVehiclePositionDriver, IVehiclePositionProvider
{
    private readonly IReadOnlyList<VehicleWorker> _pool;
    private readonly Dictionary<string, VehicleWorker> _workersByVehicleGuid = new();
    private readonly ILogger<FleetPositionDriver> _logger;

    public FleetPositionDriver(IEnumerable<VehicleWorker> workers, ILogger<FleetPositionDriver> logger)
    {
        _pool = workers.ToList();
        _logger = logger;
    }

    public Task DriveAsync(FleetStateRow row, VehicleDriveMode mode, CancellationToken cancellationToken)
    {
        var worker = ResolveWorker(row.VehicleId);
        if (worker is null)
            return Task.CompletedTask;

        return worker.DriveAsync(row, mode, cancellationToken);
    }

    public bool TryGetPosition(string vehicleId, out (double Lat, double Lng) position)
    {
        // Resolve through the SAME map the driver assigns into, but never assign here:
        // a position for a GUID that has not yet been driven does not exist.
        if (_workersByVehicleGuid.TryGetValue(vehicleId, out var worker))
            return worker.TryGetCurrentPosition(out position);

        position = default;
        return false;
    }

    // Assigns a worker to a GUID the first time the GUID is seen (sticky for the run).
    // Assignment order is fleet-state arrival order; the worker is pool[count % pool.Count].
    // The modulo is a defensive over-assignment policy: the POC has exactly 8 vehicles and
    // 8 workers, so each GUID gets a distinct worker. If more distinct GUIDs than workers
    // ever appear, assignment wraps round-robin and two GUIDs share one worker's (cosmetic)
    // loop state — acceptable for the POC; the pool is never grown.
    private VehicleWorker? ResolveWorker(string vehicleGuid)
    {
        if (_workersByVehicleGuid.TryGetValue(vehicleGuid, out var existing))
            return existing;

        if (_pool.Count == 0)
        {
            _logger.LogWarning("No VehicleWorkers in the pool; cannot drive vehicle {VehicleId}.", vehicleGuid);
            return null;
        }

        var worker = _pool[_workersByVehicleGuid.Count % _pool.Count];
        _workersByVehicleGuid[vehicleGuid] = worker;
        return worker;
    }
}
