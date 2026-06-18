using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceDelivery.Simulator.Models;
using ServiceDelivery.Simulator.Services;

namespace ServiceDelivery.Simulator.Workers;

// Orchestrates simulator startup under the per-rep identity model:
//   1. Authenticate the Simulator (position) identity FIRST — if it fails the
//      exception propagates and host startup aborts (positions cannot be posted
//      without it).
//   2. Authenticate each operated rep independently — a single rep's login
//      failure is logged and that rep is skipped; the others proceed.
//   3. Register the rep-aware job-offer handler (the SIM-005 seam: it only logs
//      which rep received which offer — no accept/decline decision lives here).
//   4. Connect every successfully-authenticated rep's RepHub.
//   5. Claim one free vehicle per automated rep (SIM-008 AC-4) so reps are
//      dispatchable before the FleetReconciler ticks.
// Implements IHostedService directly so the host awaits StartAsync before the
// FleetReconciler begins driving positions.
public sealed class SimulatorStartupService : IHostedService
{
    private readonly IIdentitySessionStore _sessionStore;
    private readonly ISignalRClient _signalRClient;
    private readonly IFleetClaimCoordinator _claimCoordinator;
    private readonly IJobOfferDecisionEngine _decisionEngine;
    private readonly ILogger<SimulatorStartupService> _logger;

    public SimulatorStartupService(
        IIdentitySessionStore sessionStore,
        ISignalRClient signalRClient,
        IFleetClaimCoordinator claimCoordinator,
        IJobOfferDecisionEngine decisionEngine,
        ILogger<SimulatorStartupService> logger)
    {
        _sessionStore = sessionStore;
        _signalRClient = signalRClient;
        _claimCoordinator = claimCoordinator;
        _decisionEngine = decisionEngine;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _sessionStore.AuthenticateAsync(_sessionStore.Simulator, cancellationToken);

        var authenticatedReps = new List<RepIdentity>();
        foreach (var rep in _sessionStore.Reps)
        {
            try
            {
                await _sessionStore.AuthenticateAsync(rep, cancellationToken);
                authenticatedReps.Add(rep);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Authentication failed for rep {Email}. Skipping this rep; others continue.",
                    rep.Email);
            }
        }

        _signalRClient.RegisterJobOfferHandler((repId, offer) =>
        {
            _logger.LogInformation(
                "JobOfferReceived for rep {RepId}: OfferId={OfferId}, RequestId={RequestId}, Tier={Tier}",
                repId, offer.OfferId, offer.RequestId, offer.RequesterTier);

            // SIM-005: delegate the accept/decline decision to the offer-triggered
            // engine. The SignalR handler is synchronous (Action), so fire-and-forget
            // the engine task with error isolation — one offer failure must not crash
            // the hub callback.
            _ = HandleOfferSafelyAsync(repId, offer);
        });

        await _signalRClient.ConnectAllAsync(authenticatedReps, cancellationToken);

        // AC-4: claim one free vehicle per automated rep so reps are dispatchable
        // Available before the FleetReconciler begins ticking.
        await _claimCoordinator.ClaimInitialVehiclesAsync(cancellationToken);
    }

    private async Task HandleOfferSafelyAsync(string repId, JobOfferPayload offer)
    {
        try
        {
            await _decisionEngine.HandleOfferAsync(repId, offer, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "Failed to handle job offer {OfferId} for rep {RepId}.", offer.OfferId, repId);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
