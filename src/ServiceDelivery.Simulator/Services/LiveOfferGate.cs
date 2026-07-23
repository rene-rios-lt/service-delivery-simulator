using System.Collections.Concurrent;

namespace ServiceDelivery.Simulator.Services;

// QUAL-029 AC-1: ConcurrentDictionary-backed per-rep latch. TryAdd is an atomic
// acquire (fails if the key is already present), TryRemove is the release — both are
// thread-safe for the concurrent SignalR-handler threads that deliver offers.
public sealed class LiveOfferGate : ILiveOfferGate
{
    private readonly ConcurrentDictionary<string, bool> _held = new();

    public bool TryAcquire(string repId) => _held.TryAdd(repId, true);

    public void Release(string repId) => _held.TryRemove(repId, out _);
}
