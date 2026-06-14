using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ServiceDelivery.Simulator.Configuration;
using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

// Manages one RepHub connection per operated rep. Each connection is built with that
// rep's own JWT so the hub joins it to rep:{repId} from the connection's own identity.
// A single rep-aware handler is invoked for every received offer; each connection's
// On<JobOfferPayload> closure captures its owning rep's RepId, so the handler always
// learns which rep the offer belongs to. A single connection failing is logged and
// skipped — the other reps still connect.
public sealed class SignalRClient : ISignalRClient
{
    private readonly SimulatorOptions _options;
    private readonly IHubConnectionFactory _factory;
    private readonly ILogger<SignalRClient> _logger;
    private readonly Dictionary<string, HubConnection> _connections = new();
    private Action<string, JobOfferPayload>? _jobOfferHandler;

    public SignalRClient(
        IOptions<SimulatorOptions> options,
        IHubConnectionFactory factory,
        ILogger<SignalRClient> logger)
    {
        _options = options.Value;
        _factory = factory;
        _logger = logger;
    }

    public void RegisterJobOfferHandler(Action<string, JobOfferPayload> handler)
    {
        _jobOfferHandler = handler;
    }

    public async Task ConnectAllAsync(IEnumerable<RepIdentity> reps, CancellationToken cancellationToken)
    {
        var hubUrl = $"{_options.BackendBaseUrl}/hubs/rep";

        foreach (var rep in reps)
        {
            var repId = rep.RepId ?? string.Empty;
            var connection = _factory.Build(hubUrl, rep.Token ?? string.Empty);

            if (_jobOfferHandler is not null)
            {
                connection.On<JobOfferPayload>(
                    "JobOfferReceived",
                    payload => _jobOfferHandler(repId, payload));
            }

            _connections[repId] = connection;

            try
            {
                await connection.StartAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to connect RepHub for rep {RepId} at {HubUrl}. Skipping this rep; others continue.",
                    repId, hubUrl);
            }
        }
    }

    // Test seam: drives the same per-connection attribution path that the real
    // On<JobOfferPayload> closure invokes, without a live hub round-trip.
    internal void InvokeJobOfferForTest(string repId, JobOfferPayload payload) =>
        _jobOfferHandler?.Invoke(repId, payload);

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections.Values)
            await connection.DisposeAsync();

        _connections.Clear();
    }
}
