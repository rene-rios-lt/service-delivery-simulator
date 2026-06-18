using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

// Decides whether the auto-decision engine should act for the rep on a fleet-state
// row. A human-controlled rep is never operated by the simulator.
public interface IRepOperationGate
{
    bool ShouldOperate(FleetStateRow row);
}
