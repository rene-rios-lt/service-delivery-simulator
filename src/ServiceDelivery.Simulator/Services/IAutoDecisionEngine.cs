using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

// The auto-decision seam (accept/decline policy, arrive/work/complete) owned by
// SIM-005. SIM-008 only invokes it for reps the RepOperationGate permits — it does
// not implement the decision policy itself.
public interface IAutoDecisionEngine
{
    Task RunAsync(FleetStateRow row, CancellationToken cancellationToken);
}
