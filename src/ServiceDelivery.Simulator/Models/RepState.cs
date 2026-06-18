namespace ServiceDelivery.Simulator.Models;

// Mirrors the backend's repState field on the fleet-state response. Drives the
// drive-mode selection in VehicleDriveResolver.
public enum RepState
{
    Offline,
    Available,
    EnRoute,
    Within15Miles,
    OnSite
}
