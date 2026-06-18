using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

// Offer-triggered decision seam: decides and executes the response to ONE job offer
// received over SignalR for a given rep. Kept separate from IAutoDecisionEngine (the
// per-tick arrive/work/complete progression) — that seam carries no OfferId and is
// triggered by the fleet-state tick, not by an offer event.
public interface IJobOfferDecisionEngine
{
    Task HandleOfferAsync(string repId, JobOfferPayload offer, CancellationToken cancellationToken);
}
