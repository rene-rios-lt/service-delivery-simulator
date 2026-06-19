namespace ServiceDelivery.Simulator.Services;

// SIM-010: a one-member time seam used by OnSiteDwellEngine to measure the on-site
// dwell across reconciler ticks. Injecting it keeps the cross-tick elapsed check
// (now - startedAt >= dwell) unit-testable with a mutable fake — no real waiting,
// no wall-clock dependency. Deliberately one member (Interface Segregation) rather
// than wrapping the broader .NET TimeProvider surface the engine does not use.
public interface ISystemClock
{
    DateTimeOffset UtcNow { get; }
}
