namespace ServiceDelivery.Simulator.Services;

// Production ISystemClock — returns wall-clock UTC. A POC dwell of 120–240s does not
// need a stopwatch-grade monotonic source; the abstraction is what makes the engine
// testable, the implementation is simply DateTimeOffset.UtcNow.
public sealed class SystemClock : ISystemClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
