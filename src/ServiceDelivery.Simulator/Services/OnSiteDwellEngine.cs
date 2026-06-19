using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

// SIM-010: drives the on-site dwell→complete progression for the reps the simulator
// operates. It observes (does NOT cause) arrival via row.RepState == OnSite — arrive
// is owned by SIM-006's ArrivalReporter. On the first OnSite tick it draws a randomized
// 120–240s dwell and records the start time; once the dwell has elapsed across later
// ticks it fires POST /rep/complete exactly once, then clears the entry. The dwell
// window is a code constant (AC-3) — the engine reads no SimulatorOptions. It runs only
// inside the reconciler's RepOperationGate; the HumanControlled short-circuit is a
// belt-and-braces guard so a mid-dwell takeover never auto-completes (AC-4).
public sealed class OnSiteDwellEngine : IAutoDecisionEngine
{
    private const int MinDwellSeconds = 120;
    private const int MaxDwellSeconds = 240;

    private readonly IBackendApiClient _apiClient;
    private readonly IDecisionRandomSource _randomSource;
    private readonly ISystemClock _clock;
    private readonly IIdentitySessionStore _sessionStore;
    private readonly ILogger<OnSiteDwellEngine> _logger;

    // Per-vehicle dwell state, keyed by VehicleId (stable across a ClaimingRepId clear).
    // Concurrent because the reconciler tick thread runs RunAsync while SignalR-driven
    // state can change between ticks — mirrors ArrivalReporter._arrivedVehicleIds.
    private readonly ConcurrentDictionary<string, DwellEntry> _dwells = new();

    private sealed record DwellEntry(DateTimeOffset StartedAt, TimeSpan Duration);

    public OnSiteDwellEngine(
        IBackendApiClient apiClient,
        IDecisionRandomSource randomSource,
        ISystemClock clock,
        IIdentitySessionStore sessionStore,
        ILogger<OnSiteDwellEngine> logger)
    {
        _apiClient = apiClient;
        _randomSource = randomSource;
        _clock = clock;
        _sessionStore = sessionStore;
        _logger = logger;
    }

    public async Task RunAsync(FleetStateRow row, CancellationToken cancellationToken)
    {
        // AC-4 belt-and-braces: the reconciler already gates this engine to automated
        // reps, but a HumanControlled row is short-circuited here too so a mid-dwell
        // takeover (or a SIM-009-yielded rep) can never auto-complete. We do NOT draw a
        // dwell and do NOT touch the entry — a stale pending entry is benign because the
        // elapsed/complete path below is unreachable for a human-controlled row.
        if (row.HumanControlled)
            return;

        // The dwell only progresses while the rep is OnSite with an active request.
        // Any other state (EnRoute before arrive, or Available after SIM-007 returns the
        // rep) clears the entry so the next job on this vehicle draws a fresh dwell.
        if (row.RepState is not RepState.OnSite || row.ActiveRequestLocation is null)
        {
            _dwells.TryRemove(row.VehicleId, out _);
            return;
        }

        if (!_dwells.TryGetValue(row.VehicleId, out var entry))
        {
            var duration = TimeSpan.FromSeconds(
                _randomSource.NextDelaySeconds(MinDwellSeconds, MaxDwellSeconds));
            _dwells[row.VehicleId] = new DwellEntry(_clock.UtcNow, duration);
            return;
        }

        if (_clock.UtcNow - entry.StartedAt < entry.Duration)
            return;

        var identity = _sessionStore.Reps.FirstOrDefault(r => r.RepId == row.ClaimingRepId);
        if (identity is null)
            return;

        _dwells.TryRemove(row.VehicleId, out _);
        await _apiClient.CompleteAsync(identity, cancellationToken);
    }
}
