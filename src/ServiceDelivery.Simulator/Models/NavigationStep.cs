namespace ServiceDelivery.Simulator.Models;

// Immutable result of one straight-line navigation step: the next position the
// vehicle should occupy. Production "reached" detection flows through the shared
// HasReached predicate on IStraightLineNavigator, so the step carries only the
// next coordinate (YAGNI — a reached flag can return when SIM-007/SIM-010 needs it).
public sealed record NavigationStep(double Lat, double Lng);
