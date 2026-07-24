using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ServiceDelivery.Simulator.Configuration;
using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

// SIM-005: decides and executes the response to a single SignalR job offer.
// One responsibility — respond to one offer. The randomness, the delay, and the
// fleet-state lookup are each their own injected seam (not inlined), so the policy
// is fully deterministic under test.
public sealed class JobOfferDecisionEngine : IJobOfferDecisionEngine
{
    private const int MinDelaySeconds = 1;
    private const int MaxDelaySeconds = 5;

    private readonly IBackendApiClient _apiClient;
    private readonly IRepOperationGate _operationGate;
    private readonly IFleetStateView _fleetStateView;
    private readonly IDecisionRandomSource _randomSource;
    private readonly IResponseDelay _responseDelay;
    private readonly IIdentitySessionStore _sessionStore;
    private readonly ILiveOfferGate _liveOfferGate;
    private readonly int _autoDeclineRatePercent;
    private readonly ILogger<JobOfferDecisionEngine> _logger;

    public JobOfferDecisionEngine(
        IBackendApiClient apiClient,
        IRepOperationGate operationGate,
        IFleetStateView fleetStateView,
        IDecisionRandomSource randomSource,
        IResponseDelay responseDelay,
        IIdentitySessionStore sessionStore,
        ILiveOfferGate liveOfferGate,
        IOptions<SimulatorOptions> options,
        ILogger<JobOfferDecisionEngine> logger)
    {
        _apiClient = apiClient;
        _operationGate = operationGate;
        _fleetStateView = fleetStateView;
        _randomSource = randomSource;
        _responseDelay = responseDelay;
        _sessionStore = sessionStore;
        _liveOfferGate = liveOfferGate;
        _autoDeclineRatePercent = options.Value.AutoDeclineRatePercent;
        _logger = logger;
    }

    public async Task HandleOfferAsync(string repId, JobOfferPayload offer, CancellationToken cancellationToken)
    {
        // QUAL-029 AC-1: a rep holds at most one live offer. If the latch is already held
        // an offer is in flight, so the rep cannot take this concurrent second one.
        if (!_liveOfferGate.TryAcquire(repId))
        {
            // BUG-062: relinquish it via decline, NOT a silent ignore. A rep with an
            // undecided offer is still Available on the backend, so an ignored second offer
            // sits Pending until ~60s expiry while the backend's idempotency guard blocks the
            // request from re-matching to a free rep (the BUG-061 stuck-Pending class). Declining
            // frees the request to re-match immediately — the same relinquish the 409 path does.
            var busyIdentity = ResolveIdentity(repId);
            if (busyIdentity is null)
            {
                _logger.LogWarning(
                    "Concurrent offer {OfferId} for rep {RepId} cannot be relinquished: no operated identity found.",
                    offer.OfferId, repId);
                return;
            }

            _logger.LogInformation(
                "Declining concurrent offer {OfferId} for rep {RepId}: an offer is already in progress.",
                offer.OfferId, repId);
            await _apiClient.DeclineJobOfferAsync(offer.OfferId, busyIdentity, cancellationToken);
            return;
        }

        // try/finally guarantees the latch is freed on every exit path — normal decision,
        // the identity-not-found early return, and cancellation.
        try
        {
            if (IsHumanControlled(repId))
            {
                _logger.LogInformation(
                    "Skipping offer {OfferId} for rep {RepId}: rep is human-controlled.", offer.OfferId, repId);
                return;
            }

            var identity = ResolveIdentity(repId);
            if (identity is null)
            {
                _logger.LogWarning(
                    "No operated identity found for rep {RepId}; cannot respond to offer {OfferId}.", repId, offer.OfferId);
                return;
            }

            var decline = _randomSource.NextPercent() < _autoDeclineRatePercent;

            var delaySeconds = _randomSource.NextDelaySeconds(MinDelaySeconds, MaxDelaySeconds);
            await _responseDelay.DelayAsync(TimeSpan.FromSeconds(delaySeconds), cancellationToken);

            if (decline)
            {
                await _apiClient.DeclineJobOfferAsync(offer.OfferId, identity, cancellationToken);
                return;
            }

            // QUAL-029 AC-2: a 409 means the rep can no longer honour the offer (already
            // busy, or the offer is no longer Pending) — decline it immediately so it is
            // not left dangling.
            var outcome = await _apiClient.AcceptJobOfferAsync(offer.OfferId, identity, cancellationToken);
            if (outcome == AcceptOutcome.Conflict)
                await _apiClient.DeclineJobOfferAsync(offer.OfferId, identity, cancellationToken);
        }
        finally
        {
            _liveOfferGate.Release(repId);
        }
    }

    // The operated identity the simulator uses to act as this rep, or null if none is held.
    private RepIdentity? ResolveIdentity(string repId) =>
        _sessionStore.Reps.FirstOrDefault(r => r.RepId == repId);

    // The simulator only connected RepHub for reps it operates, so absence of a row
    // is treated as operable (not human-controlled), not as a reason to skip.
    private bool IsHumanControlled(string repId) =>
        _fleetStateView.TryGetByRepId(repId, out var row) && row is not null && !_operationGate.ShouldOperate(row);
}
