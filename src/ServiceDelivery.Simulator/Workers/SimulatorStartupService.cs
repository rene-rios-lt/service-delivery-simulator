using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceDelivery.Simulator.Services;

namespace ServiceDelivery.Simulator.Workers;

// Orchestrates simulator startup: authenticate → register job offer handler → connect SignalR.
// Implements IHostedService directly so the host awaits StartAsync before starting VehicleWorkers,
// guaranteeing JWT and SignalR handler are in place before the first position tick.
// Connection failures are logged but do not propagate — VehicleWorkers continue independently.
public sealed class SimulatorStartupService : IHostedService
{
    private readonly IBackendApiClient _apiClient;
    private readonly ISignalRClient _signalRClient;
    private readonly ILogger<SimulatorStartupService> _logger;

    public SimulatorStartupService(
        IBackendApiClient apiClient,
        ISignalRClient signalRClient,
        ILogger<SimulatorStartupService> logger)
    {
        _apiClient = apiClient;
        _signalRClient = signalRClient;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _apiClient.AuthenticateAsync(cancellationToken);

        var jwt = _apiClient.StoredJwt ?? string.Empty;

        _signalRClient.RegisterJobOfferHandler(offer =>
        {
            _logger.LogInformation(
                "JobOfferReceived: OfferId={OfferId}, RequestId={RequestId}, Tier={Tier}",
                offer.OfferId, offer.RequestId, offer.RequesterTier);
        });

        try
        {
            await _signalRClient.ConnectAsync(jwt, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SignalR connection failed during startup. VehicleWorkers will continue independently.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
