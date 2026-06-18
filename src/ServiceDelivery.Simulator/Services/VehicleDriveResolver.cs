using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

public sealed class VehicleDriveResolver : IVehicleDriveResolver
{
    public VehicleDriveMode Resolve(FleetStateRow row) => row.RepState switch
    {
        RepState.OnSite => VehicleDriveMode.Hold,
        RepState.EnRoute or RepState.Within15Miles when row.ActiveRequestLocation is not null
            => VehicleDriveMode.Navigate,
        _ => VehicleDriveMode.IdleLoop
    };
}
