namespace ServiceDelivery.Simulator.Models;

// The position-engine mode the reconciler applies to a vehicle on a given tick,
// resolved from its fleet-state row. Navigate/Hold geometry is owned by SIM-006;
// SIM-008 owns only the mode selection.
public enum VehicleDriveMode
{
    IdleLoop,
    Navigate,
    Hold
}
