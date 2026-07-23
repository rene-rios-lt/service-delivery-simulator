namespace ServiceDelivery.Simulator.Services;

// QUAL-029 AC-2: outcome of a POST /job-offers/{id}/accept attempt. Mirrors the
// ClaimOutcome pattern (BUG-052) so the decision engine can tell a genuine 409 apart
// from a transient failure:
//   - Accepted (2xx)           — the rep is now on an active job.
//   - Conflict (409)           — rep already on an active job, or the offer is no longer
//                                Pending; the engine must decline it immediately.
//   - Failed   (other non-2xx) — transient (5xx/network); the engine takes no further
//                                action and leaves the offer alone.
public enum AcceptOutcome
{
    Accepted,
    Conflict,
    Failed
}
