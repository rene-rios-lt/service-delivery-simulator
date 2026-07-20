using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

public interface IBackendApiClient
{
    Task PostPositionAsync(VehiclePosition position, CancellationToken cancellationToken);
    Task AcceptJobOfferAsync(string offerId, RepIdentity rep, CancellationToken cancellationToken);
    Task DeclineJobOfferAsync(string offerId, RepIdentity rep, CancellationToken cancellationToken);

    // SIM-006: the automated rep marks "I've Arrived" when its truck reaches the
    // requester — POST /rep/arrive with the rep's bearer token. Fired only from the
    // gated auto-decision path so it never fires for a human-controlled rep.
    Task ArriveAsync(RepIdentity rep, CancellationToken cancellationToken);

    // SIM-010: after the randomized on-site dwell elapses, the automated rep marks the
    // work complete — POST /rep/complete with the rep's bearer token (no body). Mirrors
    // ArriveAsync; fired only from the gated dwell path so it never fires for a human rep.
    Task CompleteAsync(RepIdentity rep, CancellationToken cancellationToken);

    // SIM-008: the single authoritative fleet-state read (Simulator token) plus the
    // rep-token claim operations used at startup and during rebalance.
    Task<IReadOnlyList<FleetStateRow>> GetFleetStateAsync(CancellationToken cancellationToken);

    // BUG-052: returns a ClaimOutcome so the coordinator can distinguish a genuine 409
    // conflict (Conflict — exclude the vehicle permanently) from a transient failure
    // (Failed — leave the vehicle eligible) rather than re-selecting the same
    // front-of-list vehicle every tick. Claimed = the claim succeeded.
    Task<ClaimOutcome> ClaimVehicleAsync(string vehicleId, RepIdentity rep, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetAvailableVehicleIdsAsync(RepIdentity rep, CancellationToken cancellationToken);
}
