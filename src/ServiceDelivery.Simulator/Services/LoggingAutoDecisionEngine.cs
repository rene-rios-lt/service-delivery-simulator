using Microsoft.Extensions.Logging;
using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

// Placeholder auto-decision engine wired in by SIM-008 so the reconciler can gate and
// invoke a real collaborator for non-human reps. The accept/decline policy and the
// arrive/work/complete progression are owned by SIM-005, which will replace this
// implementation. This honours the IAutoDecisionEngine contract (it runs and records
// the operated rep) rather than throwing — it is a registered seam, not a no-op stub.
public sealed class LoggingAutoDecisionEngine : IAutoDecisionEngine
{
    private readonly ILogger<LoggingAutoDecisionEngine> _logger;

    public LoggingAutoDecisionEngine(ILogger<LoggingAutoDecisionEngine> logger)
    {
        _logger = logger;
    }

    public Task RunAsync(FleetStateRow row, CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "Auto-decision tick for operated rep {RepId} on vehicle {VehicleId} (state {RepState}). " +
            "Decision policy is implemented in SIM-005.",
            row.ClaimingRepId, row.VehicleId, row.RepState);
        return Task.CompletedTask;
    }
}
