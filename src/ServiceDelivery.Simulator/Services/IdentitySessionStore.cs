using System.Net.Http.Json;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ServiceDelivery.Simulator.Configuration;

namespace ServiceDelivery.Simulator.Services;

// Single home for authentication and per-identity session bookkeeping.
// Each identity (the Simulator position account and rep1..rep8) logs in
// independently; its JWT, decoded expiry, and decoded RepId are stored on the
// identity itself, so one identity's expiry never forces another to re-auth.
public sealed class IdentitySessionStore : IIdentitySessionStore
{
    private const int ReAuthBufferSeconds = 30;

    private readonly HttpClient _httpClient;
    private readonly ILogger<IdentitySessionStore> _logger;

    public RepIdentity Simulator { get; }
    public IReadOnlyList<RepIdentity> Reps { get; }

    public IdentitySessionStore(
        HttpClient httpClient,
        IOptions<SimulatorOptions> options,
        ILogger<IdentitySessionStore> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        var simulatorOptions = options.Value;

        Simulator = new RepIdentity
        {
            Email = simulatorOptions.SimulatorEmail,
            Password = simulatorOptions.SimulatorPassword,
            Role = IdentityRole.Simulator
        };

        Reps = simulatorOptions.RepEmails
            .Select(email => new RepIdentity
            {
                Email = email,
                Password = simulatorOptions.RepPassword,
                Role = IdentityRole.ServiceRep
            })
            .ToList();
    }

    public async Task AuthenticateAsync(RepIdentity identity, CancellationToken cancellationToken)
    {
        var payload = new { email = identity.Email, password = identity.Password };
        var response = await _httpClient.PostAsJsonAsync("/auth/login", payload, cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new AuthenticationException($"Authentication failed with status {response.StatusCode}.");

        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);

        var token = doc.RootElement.GetProperty("token").GetString()
            ?? throw new AuthenticationException("Backend returned an empty token.");

        identity.Token = token;
        identity.TokenExpiry = ExtractExpiry(token);
        identity.RepId = ExtractSubject(token);
    }

    public async Task<string> GetValidTokenAsync(RepIdentity identity, CancellationToken cancellationToken)
    {
        if (identity.Token is null || IsTokenExpiredOrNearExpiry(identity.TokenExpiry))
            await AuthenticateAsync(identity, cancellationToken);

        return identity.Token!;
    }

    private static bool IsTokenExpiredOrNearExpiry(DateTime expiry)
    {
        if (expiry == DateTime.MinValue)
            return false;

        return DateTime.UtcNow >= expiry.AddSeconds(-ReAuthBufferSeconds);
    }

    private static DateTime ExtractExpiry(string jwt)
    {
        var payload = TryReadPayload(jwt);
        if (payload is null)
            return DateTime.MinValue;

        if (payload.Value.TryGetProperty("exp", out var expElement))
            return DateTimeOffset.FromUnixTimeSeconds(expElement.GetInt64()).UtcDateTime;

        return DateTime.MinValue;
    }

    private static string? ExtractSubject(string jwt)
    {
        var payload = TryReadPayload(jwt);
        if (payload is null)
            return null;

        return payload.Value.TryGetProperty("sub", out var subElement)
            ? subElement.GetString()
            : null;
    }

    private static JsonElement? TryReadPayload(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3)
            return null;

        try
        {
            var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            using var doc = JsonDocument.Parse(payloadJson);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
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
