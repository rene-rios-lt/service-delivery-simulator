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
//
// BUG-053: each connection's Reconnecting/Reconnected/Closed lifecycle is logged
// per rep (so a deaf connection is visible instead of silent), and a Closed
// connection triggers an indefinite jittered-backoff restart loop so the rep heals
// without a process restart. EnsureConnectedAsync lets the reconciler tick request a
// per-rep health check and restart a connection that is no longer Connected.
public sealed class SignalRClient : ISignalRClient
{
    private readonly SimulatorOptions _options;
    private readonly IHubConnectionFactory _factory;
    private readonly ILogger<SignalRClient> _logger;
    private readonly Dictionary<string, HubConnection> _connections = new();

    // Lives for the whole simulator run. The per-rep self-healing restart loops
    // watch this token so they retry indefinitely until DisposeAsync cancels it.
    private readonly CancellationTokenSource _lifetimeCts = new();
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
        var repList = reps as IReadOnlyCollection<RepIdentity> ?? reps.ToList();
        var connectedCount = 0;

        foreach (var rep in repList)
        {
            var repId = rep.RepId ?? string.Empty;
            var connection = _factory.Build(hubUrl, rep.Token ?? string.Empty);

            if (_jobOfferHandler is not null)
            {
                connection.On<JobOfferPayload>(
                    "JobOfferReceived",
                    payload => _jobOfferHandler(repId, payload));
            }

            connection.Reconnecting += error =>
            {
                OnReconnecting(repId, error);
                return Task.CompletedTask;
            };

            connection.Reconnected += connectionId =>
            {
                OnReconnected(repId, connectionId);
                return Task.CompletedTask;
            };

            connection.Closed += error =>
            {
                OnClosed(repId, error);
                return Task.CompletedTask;
            };

            _connections[repId] = connection;

            try
            {
                await connection.StartAsync(cancellationToken);
                connectedCount++;
                _logger.LogInformation("RepHub connected for rep {RepId}.", repId);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to connect RepHub for rep {RepId} at {HubUrl}. Skipping this rep; others continue.",
                    repId, hubUrl);
            }
        }

        _logger.LogInformation("{Connected}/{Total} rep hubs connected.", connectedCount, repList.Count);
    }

    public async Task EnsureConnectedAsync(string repId, CancellationToken cancellationToken)
    {
        if (!_connections.TryGetValue(repId, out var connection))
            return;

        if (connection.State == HubConnectionState.Connected)
            return;

        _logger.LogWarning(
            "RepHub for rep {RepId} is {State}; attempting restart.",
            repId, connection.State);

        try
        {
            await connection.StartAsync(cancellationToken);
            _logger.LogInformation("RepHub restarted for rep {RepId}.", repId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RepHub restart attempt failed for rep {RepId}.", repId);
        }
    }

    // Fires when the connection drops and automatic-reconnect starts retrying. Logged
    // per rep so a flapping connection is visible in the simulator log.
    private void OnReconnecting(string repId, Exception? error) =>
        _logger.LogWarning(
            error,
            "RepHub connection reconnecting for rep {RepId}.",
            repId);

    // Fires when automatic-reconnect succeeds and the connection is live again.
    // Logged per rep so recovery is visible after a reconnecting spell.
    private void OnReconnected(string repId, string? connectionId) =>
        _logger.LogInformation(
            "RepHub connection reconnected for rep {RepId} (connection {ConnectionId}).",
            repId, connectionId);

    // Fires when a connection's automatic-reconnect budget is exhausted and it goes
    // permanently Disconnected. Logs the closure against the owning rep (so a deaf
    // connection is visible instead of silent) and starts an indefinite jittered
    // backoff restart loop so the rep heals without a process restart.
    private void OnClosed(string repId, Exception? error)
    {
        _logger.LogWarning(
            error,
            "RepHub connection closed for rep {RepId}. Scheduling restart attempts.",
            repId);

        if (_lifetimeCts.IsCancellationRequested)
            return;

        _ = RestartWithBackoffAsync(repId, _lifetimeCts.Token);
    }

    private async Task RestartWithBackoffAsync(string repId, CancellationToken lifetime)
    {
        var jitter = new Random();

        while (!lifetime.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(5) + TimeSpan.FromMilliseconds(jitter.Next(0, 2_000));

            try
            {
                await Task.Delay(delay, lifetime);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!_connections.TryGetValue(repId, out var connection))
                return;

            if (connection.State == HubConnectionState.Connected)
                return;

            try
            {
                await connection.StartAsync(lifetime);
                _logger.LogInformation("RepHub reconnected for rep {RepId} after backoff restart.", repId);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RepHub backoff restart attempt failed for rep {RepId}. Will retry.", repId);
            }
        }
    }

    // Test seam: drives the same per-connection attribution path that the real
    // On<JobOfferPayload> closure invokes, without a live hub round-trip.
    internal void InvokeJobOfferForTest(string repId, JobOfferPayload payload) =>
        _jobOfferHandler?.Invoke(repId, payload);

    // Test seam: invokes the Reconnecting lifecycle handler directly, mirroring the
    // real connection.Reconnecting event, without a live hub round-trip.
    internal void SimulateReconnectingForTest(string repId, Exception? error) =>
        OnReconnecting(repId, error);

    // Test seam: invokes the Reconnected lifecycle handler directly, mirroring the
    // real connection.Reconnected event, without a live hub round-trip.
    internal void SimulateReconnectedForTest(string repId, string? connectionId) =>
        OnReconnected(repId, connectionId);

    // Test seam: invokes the Closed lifecycle handler directly, mirroring the real
    // connection.Closed event, without a live hub round-trip.
    internal void SimulateClosedForTest(string repId, Exception? error) =>
        OnClosed(repId, error);

    public async ValueTask DisposeAsync()
    {
        await _lifetimeCts.CancelAsync();

        foreach (var connection in _connections.Values)
            await connection.DisposeAsync();

        _connections.Clear();
        _lifetimeCts.Dispose();
    }
}
