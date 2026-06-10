using System.Net;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ServiceDelivery.Simulator.Configuration;
using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

// Wraps all HTTP calls to the backend API.
// Handles authentication (login → JWT), bearer-header propagation, JWT-expiry
// re-authentication, 401-retry logic, and vehicle position updates.
public sealed class BackendApiClient : IBackendApiClient
{
    private const int ReAuthBufferSeconds = 30;

    private readonly HttpClient _httpClient;
    private readonly SimulatorOptions _options;
    private readonly ILogger<BackendApiClient> _logger;
    private string? _jwt;
    private DateTime _jwtExpiry = DateTime.MinValue;

    // Exposed for test inspection — allows asserting the JWT was received and stored.
    public string? StoredJwt => _jwt;

    public BackendApiClient(
        HttpClient httpClient,
        IOptions<SimulatorOptions> options,
        ILogger<BackendApiClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _httpClient.BaseAddress = new Uri(_options.BackendBaseUrl);
    }

    public async Task AuthenticateAsync(CancellationToken cancellationToken)
    {
        var payload = new { email = _options.SimulatorEmail, password = _options.SimulatorPassword };
        var response = await _httpClient.PostAsJsonAsync("/auth/login", payload, cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new AuthenticationException($"Authentication failed with status {response.StatusCode}.");

        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);

        _jwt = doc.RootElement.GetProperty("token").GetString()
            ?? throw new AuthenticationException("Backend returned an empty token.");

        _jwtExpiry = ExtractExpiry(_jwt);

        _httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _jwt);
    }

    public async Task PostPositionAsync(VehiclePosition position, CancellationToken cancellationToken)
    {
        await EnsureAuthenticatedAsync(cancellationToken);
        var response = await _httpClient.PostAsJsonAsync(
            $"/vehicles/{position.VehicleId}/position",
            new { position.Latitude, position.Longitude },
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await AuthenticateAsync(cancellationToken);
            var retryResponse = await _httpClient.PostAsJsonAsync(
                $"/vehicles/{position.VehicleId}/position",
                new { position.Latitude, position.Longitude },
                cancellationToken);

            if (!retryResponse.IsSuccessStatusCode)
                _logger.LogError(
                    "POST /vehicles/{VehicleId}/position returned {StatusCode} after re-authentication.",
                    position.VehicleId,
                    retryResponse.StatusCode);
        }
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

    private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        if (_jwt is null || IsTokenExpiredOrNearExpiry())
            await AuthenticateAsync(cancellationToken);
    }

    private bool IsTokenExpiredOrNearExpiry()
    {
        if (_jwtExpiry == DateTime.MinValue)
            return false;

        return DateTime.UtcNow >= _jwtExpiry.AddSeconds(-ReAuthBufferSeconds);
    }

    private static DateTime ExtractExpiry(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3)
            return DateTime.MinValue;

        try
        {
            var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.TryGetProperty("exp", out var expElement))
            {
                var unixSeconds = expElement.GetInt64();
                return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
            }
        }
        catch
        {
            // If the JWT is malformed, treat expiry as unknown — no re-auth forced
        }

        return DateTime.MinValue;
    }

    private static byte[] Base64UrlDecode(string base64Url)
    {
        var padded = base64Url
            .Replace('-', '+')
            .Replace('_', '/');

        var padding = padded.Length % 4;
        if (padding != 0)
            padded += new string('=', 4 - padding);

        return Convert.FromBase64String(padded);
    }
}
