using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using ServiceDelivery.Simulator.Configuration;

namespace ServiceDelivery.Simulator.Services;

// Connects to the backend's RepHub to receive job offers for the simulator service account.
// The simulator auto-accepts (~85%) or auto-declines (~15%) each offer.
public sealed class SignalRClient : IAsyncDisposable
{
    private readonly SimulatorOptions _options;
    private HubConnection? _connection;

    public SignalRClient(IOptions<SimulatorOptions> options)
    {
        _options = options.Value;
    }

    public async Task ConnectAsync(string jwt, CancellationToken cancellationToken)
    {
        // TODO: Build HubConnection to {BackendBaseUrl}/hubs/rep with JWT bearer token
        // TODO: Register handler for "JobOfferReceived" event
        throw new NotImplementedException();
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}
