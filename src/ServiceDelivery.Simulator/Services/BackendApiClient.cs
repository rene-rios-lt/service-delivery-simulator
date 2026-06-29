using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

// Wraps all HTTP calls to the backend API. Authentication and per-identity token
// bookkeeping live in IIdentitySessionStore — this client only attaches the right
// identity's bearer token to each outgoing request:
//   - position posts use the Simulator identity's token
//   - accept/decline use the responding rep's token
// Each request carries its own Authorization header (no shared DefaultRequestHeaders),
// so the single HttpClient can serve multiple identities concurrently.
public sealed class BackendApiClient : IBackendApiClient
{
    private readonly HttpClient _httpClient;
    private readonly IIdentitySessionStore _sessionStore;
    private readonly ILogger<BackendApiClient> _logger;

    public BackendApiClient(
        HttpClient httpClient,
        IIdentitySessionStore sessionStore,
        ILogger<BackendApiClient> logger)
    {
        _httpClient = httpClient;
        _sessionStore = sessionStore;
        _logger = logger;
    }

    public async Task PostPositionAsync(VehiclePosition position, CancellationToken cancellationToken)
    {
        var simulator = _sessionStore.Simulator;
        var token = await _sessionStore.GetValidTokenAsync(simulator, cancellationToken);

        using (var response = await SendPositionAsync(position, token, cancellationToken))
        {
            if (response.StatusCode != HttpStatusCode.Unauthorized)
                return;
        }

        await _sessionStore.AuthenticateAsync(simulator, cancellationToken);
        using var retryResponse = await SendPositionAsync(position, simulator.Token!, cancellationToken);

        if (!retryResponse.IsSuccessStatusCode)
            _logger.LogError(
                "POST /vehicles/{VehicleId}/position returned {StatusCode} after re-authentication.",
                position.VehicleId,
                retryResponse.StatusCode);
    }

    public async Task ArriveAsync(RepIdentity rep, CancellationToken cancellationToken)
    {
        var token = await _sessionStore.GetValidTokenAsync(rep, cancellationToken);
        using var request = BuildAuthorizedRequest(HttpMethod.Post, "/rep/arrive", token);
        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
            _logger.LogError(
                "POST /rep/arrive returned {StatusCode} for rep {RepId}.",
                response.StatusCode, rep.RepId);
    }

    public async Task CompleteAsync(RepIdentity rep, CancellationToken cancellationToken)
    {
        var token = await _sessionStore.GetValidTokenAsync(rep, cancellationToken);
        using var request = BuildAuthorizedRequest(HttpMethod.Post, "/rep/complete", token);
        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
            _logger.LogError(
                "POST /rep/complete returned {StatusCode} for rep {RepId}.",
                response.StatusCode, rep.RepId);
    }

    public Task AcceptJobOfferAsync(string offerId, RepIdentity rep, CancellationToken cancellationToken) =>
        PostJobOfferActionAsync(offerId, "accept", rep, cancellationToken);

    public Task DeclineJobOfferAsync(string offerId, RepIdentity rep, CancellationToken cancellationToken) =>
        PostJobOfferActionAsync(offerId, "decline", rep, cancellationToken);

    private async Task PostJobOfferActionAsync(
        string offerId, string action, RepIdentity rep, CancellationToken cancellationToken)
    {
        var token = await _sessionStore.GetValidTokenAsync(rep, cancellationToken);
        using var request = BuildAuthorizedRequest(HttpMethod.Post, $"/job-offers/{offerId}/{action}", token);
        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
            _logger.LogError(
                "POST /job-offers/{OfferId}/{Action} returned {StatusCode} for rep {RepId}.",
                offerId, action, response.StatusCode, rep.RepId);
    }

    // Wire enums must arrive as the enum NAME string. An integer or an unmapped name
    // is schema drift and must THROW, never silently bind to a bogus value
    // (allowIntegerValues: false closes the residual integer path — see QUAL-006 /
    // ADR-0011 / BUG-016 / BUG-036).
    private static readonly JsonSerializerOptions FleetStateJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }
    };

    public Task<IReadOnlyList<FleetStateRow>> GetFleetStateAsync(CancellationToken cancellationToken) =>
        GetJsonAsync<FleetStateRow>(_sessionStore.Simulator, "/simulator/fleet-state", cancellationToken);

    public async Task<IReadOnlyList<string>> GetAvailableVehicleIdsAsync(
        RepIdentity rep, CancellationToken cancellationToken)
    {
        var vehicles = await GetJsonAsync<AvailableVehicle>(rep, "/vehicles/available", cancellationToken);
        return vehicles.Select(v => v.VehicleId).ToList();
    }

    public async Task ClaimVehicleAsync(string vehicleId, RepIdentity rep, CancellationToken cancellationToken)
    {
        var token = await _sessionStore.GetValidTokenAsync(rep, cancellationToken);
        using var request = BuildAuthorizedRequest(HttpMethod.Post, $"/vehicles/{vehicleId}/claim", token);
        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
            _logger.LogError(
                "POST /vehicles/{VehicleId}/claim returned {StatusCode} for rep {RepId}.",
                vehicleId, response.StatusCode, rep.RepId);
    }

    private async Task<IReadOnlyList<T>> GetJsonAsync<T>(
        RepIdentity identity, string relativeUri, CancellationToken cancellationToken)
    {
        var token = await _sessionStore.GetValidTokenAsync(identity, cancellationToken);
        using var request = BuildAuthorizedRequest(HttpMethod.Get, relativeUri, token);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var items = await response.Content.ReadFromJsonAsync<List<T>>(FleetStateJsonOptions, cancellationToken);
        return items ?? [];
    }

    private async Task<HttpResponseMessage> SendPositionAsync(
        VehiclePosition position, string token, CancellationToken cancellationToken)
    {
        using var request = BuildAuthorizedRequest(
            HttpMethod.Post, $"/vehicles/{position.VehicleId}/position", token);
        request.Content = JsonContent.Create(new { position.Latitude, position.Longitude });
        return await _httpClient.SendAsync(request, cancellationToken);
    }

    private static HttpRequestMessage BuildAuthorizedRequest(HttpMethod method, string relativeUri, string token)
    {
        var request = new HttpRequestMessage(method, relativeUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
}
