namespace ServiceDelivery.Simulator.Services;

// BUG-052: outcome of a POST /vehicles/{id}/claim attempt. The coordinator must be
// able to tell a genuine conflict apart from a transient failure:
//   - Claimed  (2xx)          — this rep now holds the vehicle.
//   - Conflict (409)          — another rep holds it; exclude it from further candidates.
//   - Failed   (other non-2xx)— transient (5xx/network); the vehicle may still be free,
//                               so it must remain eligible for a later attempt.
// Conflating Conflict and Failed permanently starves a genuinely-free vehicle that hit
// a transient blip — the exact class of bug BUG-052 fixes.
public enum ClaimOutcome
{
    Claimed,
    Conflict,
    Failed
}
