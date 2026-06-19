using ServiceDelivery.Simulator.Services;

namespace ServiceDelivery.Simulator.Tests.Services;

// Trivial mutable ISystemClock test double. Tests set UtcNow at arrange time and
// advance it by assignment between RunAsync calls to simulate dwell elapsing across
// reconciler ticks — fully deterministic, no real waiting.
internal sealed class FakeClock : ISystemClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch;

    public void Advance(TimeSpan by) => UtcNow += by;
}
