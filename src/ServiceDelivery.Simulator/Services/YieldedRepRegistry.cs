using System.Collections.Concurrent;
using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

// Thread-safe, process-lifetime store of yielded reps and vehicles. Two
// ConcurrentDictionary<string, byte> instances act as add-only sets: the reconciler
// tick thread writes via ObserveAndRecordIfYielded while the gate (reconciler thread)
// and JobOfferDecisionEngine (SignalR handler thread) read via the IsYielded queries.
// Nothing ever removes an entry, so a yield is sticky for the lifetime of the run.
public sealed class YieldedRepRegistry : IYieldedRepRegistry
{
    private readonly ConcurrentDictionary<string, byte> _yieldedReps = new();
    private readonly ConcurrentDictionary<string, byte> _yieldedVehicles = new();

    public void ObserveAndRecordIfYielded(FleetStateRow row)
    {
        if (!row.HumanControlled)
            return;

        if (row.ClaimingRepId is not null)
            _yieldedReps.TryAdd(row.ClaimingRepId, 0);

        _yieldedVehicles.TryAdd(row.VehicleId, 0);
    }

    public bool IsRepYielded(string? repId) =>
        repId is not null && _yieldedReps.ContainsKey(repId);

    public bool IsVehicleYielded(string vehicleId) =>
        _yieldedVehicles.ContainsKey(vehicleId);
}
