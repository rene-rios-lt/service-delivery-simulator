using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

// Drives a single vehicle's position for one tick given its fleet-state row and the
// resolved drive mode. Implemented by VehicleWorker. The navigate/hold geometry is
// owned by SIM-006; SIM-008 dispatches the IdleLoop branch and leaves the seam.
public interface IVehiclePositionDriver
{
    Task DriveAsync(FleetStateRow row, VehicleDriveMode mode, CancellationToken cancellationToken);
}
