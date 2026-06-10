using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ServiceDelivery.Simulator.Configuration;
using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

// Connects to the backend's RepHub to receive job offers for the simulator service account.
// The simulator auto-accepts (~85%) or auto-declines (~15%) each offer.
public sealed class SignalRClient : ISignalRClient
{
    private readonly SimulatorOptions _options;
    private readonly IHubConnectionFactory _factory;
    private readonly ILogger<SignalRClient> _logger;
    private HubConnection? _connection;
    private Action<JobOfferPayload>? _jobOfferHandler;
    internal Action<JobOfferPayload>? JobOfferHandlerForTest => _jobOfferHandler;

    public SignalRClient(
        IOptions<SimulatorOptions> options,
        IHubConnectionFactory factory,
        ILogger<SignalRClient> logger)
    {
        _options = options.Value;
        _factory = factory;
        _logger = logger;
    }

    public void RegisterJobOfferHandler(Action<JobOfferPayload> handler)
    {
        _jobOfferHandler = handler;
    }

    public async Task ConnectAsync(string jwt, CancellationToken cancellationToken)
    {
        var hubUrl = $"{_options.BackendBaseUrl}/hubs/rep";
        _connection = _factory.Build(hubUrl, jwt);

        if (_jobOfferHandler is not null)
        {
            _connection.On<JobOfferPayload>("JobOfferReceived", _jobOfferHandler);
        }

        try
        {
            await _connection.StartAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to RepHub at {HubUrl}. Workers will continue independently.", hubUrl);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}
