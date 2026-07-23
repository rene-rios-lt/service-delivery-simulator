namespace ServiceDelivery.Simulator.Services;

// QUAL-029 AC-1: the per-rep "one offer at a time" latch. Separated from the decision
// engine so the engine's single responsibility stays "decide on one offer" while this
// type's single responsibility is "prevent concurrent offers for the same rep".
public interface ILiveOfferGate
{
    // Atomically acquires the latch for this rep.
    // Returns true if the latch was free; false if another offer is already in flight.
    bool TryAcquire(string repId);

    // Releases the latch. Called from a finally block — always fires.
    void Release(string repId);
}
