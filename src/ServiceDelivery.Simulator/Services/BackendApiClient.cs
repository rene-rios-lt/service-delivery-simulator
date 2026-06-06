using Microsoft.Extensions.Options;
using ServiceDelivery.Simulator.Configuration;
using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

// Wraps all HTTP calls to the backend API.
// Handles authentication (login → JWT) and vehicle position updates.
public sealed class BackendApiClient : IBackendApiClient
{
    private readonly HttpClient _httpClient;
    private readonly SimulatorOptions _options;
    private string? _jwt;

    public BackendApiClient(HttpClient httpClient, IOptions<SimulatorOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _httpClient.BaseAddress = new Uri(_options.BackendBaseUrl);
    }

    public async Task AuthenticateAsync(CancellationToken cancellationToken)
    {
        // TODO: POST /auth/login with simulator credentials, store JWT
        throw new NotImplementedException();
    }

    public async Task PostPositionAsync(VehiclePosition position, CancellationToken cancellationToken)
    {
        // TODO: POST /vehicles/{id}/position with current lat/lng
        throw new NotImplementedException();
    }

    public async Task AcceptJobOfferAsync(string offerId, CancellationToken cancellationToken)
    {
        // TODO: POST /job-offers/{id}/accept
        throw new NotImplementedException();
    }

    public async Task DeclineJobOfferAsync(string offerId, CancellationToken cancellationToken)
    {
        // TODO: POST /job-offers/{id}/decline
        throw new NotImplementedException();
    }
}
