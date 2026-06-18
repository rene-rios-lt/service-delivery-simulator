using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

public sealed class RepOperationGate : IRepOperationGate
{
    private readonly IYieldedRepRegistry _yieldedReps;

    public RepOperationGate(IYieldedRepRegistry yieldedReps)
    {
        _yieldedReps = yieldedReps;
    }

    // A pure query: the rep is operable only if it is not currently human-controlled
    // AND neither its rep nor its vehicle was ever yielded to a human earlier in the
    // run (sticky). The vehicle id is checked too because an off-duty park clears
    // ClaimingRepId on the row while the vehicle id remains the stable identifier.
    // Reads the registry; never mutates it — observation is the reconciler's job (SRP).
    public bool ShouldOperate(FleetStateRow row) =>
        !row.HumanControlled
        && !_yieldedReps.IsRepYielded(row.ClaimingRepId)
        && !_yieldedReps.IsVehicleYielded(row.VehicleId);
}
