namespace ServiceDelivery.Simulator.Services;

// Production randomness for the job-offer decision. Uses Random.Shared, which is
// thread-safe, so a single registered instance is safe across the reconciler and the
// SignalR handler threads.
public sealed class DefaultDecisionRandomSource : IDecisionRandomSource
{
    public int NextPercent() => Random.Shared.Next(0, 100);

    public int NextDelaySeconds(int minInclusive, int maxInclusive) =>
        Random.Shared.Next(minInclusive, maxInclusive + 1);
}
