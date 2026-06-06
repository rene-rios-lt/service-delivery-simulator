namespace ServiceDelivery.Simulator.Models;

public sealed class VehicleRoute
{
    public required string VehicleId { get; init; }
    public required IReadOnlyList<RouteWaypoint> Waypoints { get; init; }
}
