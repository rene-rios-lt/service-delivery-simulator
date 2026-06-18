using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

// SIM-006: the automated-only arrive handoff to SIM-010. When an operated rep's truck
// first reaches the requester, fires IBackendApiClient.ArriveAsync exactly once. Gated
// automated-only so it never fires for a human-controlled rep; idempotent per arrival.
public interface IArrivalReporter
{
    Task ReportArrivalIfReachedAsync(FleetStateRow row, CancellationToken cancellationToken);
}
