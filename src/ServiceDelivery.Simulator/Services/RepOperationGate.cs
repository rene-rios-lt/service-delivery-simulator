using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

public sealed class RepOperationGate : IRepOperationGate
{
    public bool ShouldOperate(FleetStateRow row) => !row.HumanControlled;
}
