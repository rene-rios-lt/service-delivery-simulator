namespace ServiceDelivery.Simulator.Models;

// One item of the backend GET /vehicles/available response: an unclaimed vehicle a
// rep may claim. Only VehicleId is load-bearing (the caller projects out the id);
// Registration and Equipment are included for fidelity to the wire shape and are
// nullable to tolerate absent/partial fields. PascalCase binds to the backend's
// camelCase via PropertyNameCaseInsensitive (see BackendApiClient.FleetStateJsonOptions).
public sealed record AvailableVehicle(
    string VehicleId,
    string? Registration,
    IReadOnlyList<string>? Equipment);
