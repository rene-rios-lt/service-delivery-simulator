using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

// SIM-009: process-lifetime, thread-safe memory of reps/vehicles yielded to a human.
// The reconciler tick records a yield the moment a row is first observed
// human-controlled; the operation gate and claim coordinator consult it read-only.
// Yields are sticky — never cleared for the lifetime of the run — so a rep is never
// re-assumed and its vehicle is never re-claimed once a human has taken over.
public interface IYieldedRepRegistry
{
    // Records (ClaimingRepId, VehicleId) when row.HumanControlled is true. Idempotent;
    // no-op for non-human rows; never clears a recorded yield.
    void ObserveAndRecordIfYielded(FleetStateRow row);

    bool IsRepYielded(string? repId);
    bool IsVehicleYielded(string vehicleId);
}
