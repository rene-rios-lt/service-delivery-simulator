namespace ServiceDelivery.Simulator.Services;

// Narrow read-only lookup of a vehicle's current simulator-tracked position. Implemented
// by the FleetPositionDriver, which already owns one VehicleWorker per vehicle. The
// ArrivalReporter uses this to recompute "reached" against the requester without the
// position driver itself ever making a rep action call.
public interface IVehiclePositionProvider
{
    bool TryGetPosition(string vehicleId, out (double Lat, double Lng) position);
}
