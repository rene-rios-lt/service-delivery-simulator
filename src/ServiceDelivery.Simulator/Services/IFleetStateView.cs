using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

// Holds the most-recent fleet snapshot so the offer-triggered decision engine can
// resolve a rep's human-control state at offer time. Written by the FleetReconciler
// once per tick; read by the SignalR job-offer handler thread — implementations must
// be thread-safe.
public interface IFleetStateView
{
    void Publish(IReadOnlyList<FleetStateRow> snapshot);
    bool TryGetByRepId(string repId, out FleetStateRow? row);
}
