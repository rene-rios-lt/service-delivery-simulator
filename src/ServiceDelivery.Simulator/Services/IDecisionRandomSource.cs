namespace ServiceDelivery.Simulator.Services;

// Deterministic randomness seam for the job-offer decision. Injecting it lets tests
// force an exact accept/decline outcome and an exact response delay without relying
// on real probability or wall-clock timing.
public interface IDecisionRandomSource
{
    // Returns a value in [0, 99] used against AutoDeclineRatePercent.
    int NextPercent();

    // Returns a value in [minInclusive, maxInclusive] — the response delay seconds.
    int NextDelaySeconds(int minInclusive, int maxInclusive);
}
