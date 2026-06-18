namespace ServiceDelivery.Simulator.Models;

// One row of the backend GET /simulator/fleet-state response: the authoritative
// per-vehicle snapshot the reconciler reads once per tick. ActiveRequestLocation is
// null when the vehicle has no in-flight request.
public sealed record FleetStateRow(
    string VehicleId,
    string? ClaimingRepId,
    RepState RepState,
    bool HumanControlled,
    RequesterLocation? ActiveRequestLocation);
