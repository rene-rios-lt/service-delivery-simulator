using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
// Implements IHostedService directly so the host awaits StartAsync before the
// VehicleWorkers begin posting positions.
public sealed class SimulatorStartupService : IHostedService
{
    private readonly IIdentitySessionStore _sessionStore;
    private readonly ISignalRClient _signalRClient;
    private readonly ILogger<SimulatorStartupService> _logger;

    public SimulatorStartupService(
        IIdentitySessionStore sessionStore,
        ISignalRClient signalRClient,
        ILogger<SimulatorStartupService> logger)
    {
        _sessionStore = sessionStore;
        _signalRClient = signalRClient;
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
        });

        await _signalRClient.ConnectAllAsync(authenticatedReps, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
