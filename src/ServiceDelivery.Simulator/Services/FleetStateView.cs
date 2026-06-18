using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

// Thread-safe holder for the most-recent fleet snapshot, keyed by claiming rep id.
// Written by the FleetReconciler tick and read by the SignalR offer handler thread.
// Publishing swaps the whole map reference atomically so a concurrent reader always
// sees a complete snapshot (never a half-cleared one).
public sealed class FleetStateView : IFleetStateView
{
    private volatile IReadOnlyDictionary<string, FleetStateRow> _byRepId =
        new Dictionary<string, FleetStateRow>();

    public void Publish(IReadOnlyList<FleetStateRow> snapshot)
    {
        var next = new Dictionary<string, FleetStateRow>();
        foreach (var row in snapshot)
        {
            if (row.ClaimingRepId is not null)
                next[row.ClaimingRepId] = row;
        }

        _byRepId = next;
    }

    public bool TryGetByRepId(string repId, out FleetStateRow? row)
    {
        if (_byRepId.TryGetValue(repId, out var found))
        {
            row = found;
            return true;
        }

        row = null;
        return false;
    }
}
