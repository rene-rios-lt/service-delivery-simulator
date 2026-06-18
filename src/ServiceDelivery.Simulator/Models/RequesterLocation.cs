namespace ServiceDelivery.Simulator.Models;

// The active service request's location for a vehicle, as reported by the backend
// fleet-state read. Null when the vehicle has no active request.
public sealed record RequesterLocation(double Lat, double Lng);
