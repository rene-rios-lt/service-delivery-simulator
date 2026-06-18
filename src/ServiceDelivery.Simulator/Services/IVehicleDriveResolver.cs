using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

// Maps a single fleet-state row to the position-engine drive mode for that vehicle
// this tick. Pure decision — no HTTP, no state.
public interface IVehicleDriveResolver
{
    VehicleDriveMode Resolve(FleetStateRow row);
}
