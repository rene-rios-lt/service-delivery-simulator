using System.Net;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using ServiceDelivery.Simulator.Configuration;
using ServiceDelivery.Simulator.Services;
using Xunit;

namespace ServiceDelivery.Simulator.Tests.Services;

public class IdentitySessionStoreTests
{
    // ─── Helpers ───────────────────────────────────────────────────────────────

    private static SimulatorOptions DefaultOptions(string baseUrl = "https://backend.local") =>
        new SimulatorOptions { BackendBaseUrl = baseUrl };

    private static IdentitySessionStore BuildStore(
        SimulatorOptions options,
        Func<int, HttpResponseMessage> responder,
        out List<HttpRequestMessage> capturedRequests)
    {
        var requests = new List<HttpRequestMessage>();
        capturedRequests = requests;

        var handlerMock = new Mock<HttpMessageHandler>();
        var callCount = 0;
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => requests.Add(req))
            .ReturnsAsync(() => responder(++callCount));

        var httpClient = new HttpClient(handlerMock.Object) { BaseAddress = new Uri(options.BackendBaseUrl) };
        return new IdentitySessionStore(httpClient, Options.Create(options), NullLogger<IdentitySessionStore>.Instance);
    }

    private static HttpResponseMessage OkLoginResponse(string token) =>
        new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { token }),
                Encoding.UTF8,
                "application/json")
        };

    private static RepIdentity RepIdentity(string email = "rep1@dealer.com", string password = "pw") =>
        new RepIdentity { Email = email, Password = password, Role = IdentityRole.ServiceRep };

    private static RepIdentity SimulatorIdentity(string email = "sim@system.internal", string password = "pw") =>
        new RepIdentity { Email = email, Password = password, Role = IdentityRole.Simulator };

    // ─── AC-2 ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GivenARepIdentity_WhenAuthenticated_ThenItsOwnTokenIsStored()
    {
        // Arrange
        var store = BuildStore(DefaultOptions(), _ => OkLoginResponse("rep-token"), out _);
        var rep = RepIdentity();

        // Act
        await store.AuthenticateAsync(rep, CancellationToken.None);

        // Assert
        Assert.Equal("rep-token", rep.Token);
    }

    [Fact]
    public async Task GivenMultipleIdentities_WhenEachAuthenticated_ThenEachHoldsItsOwnDistinctToken()
    {
        // Arrange
        var repA = RepIdentity(email: "rep1@dealer.com");
        var repB = RepIdentity(email: "rep2@dealer.com");
        var store = BuildStore(
            DefaultOptions(),
            call => call == 1 ? OkLoginResponse("token-A") : OkLoginResponse("token-B"),
            out _);

        // Act
        await store.AuthenticateAsync(repA, CancellationToken.None);
        await store.AuthenticateAsync(repB, CancellationToken.None);

        // Assert
        Assert.Equal("token-A", repA.Token);
        Assert.Equal("token-B", repB.Token);
    }

    [Fact]
    public async Task GivenAnExpiredRepToken_WhenGetValidTokenCalled_ThenThatIdentityReAuthenticates()
    {
        // Arrange — first login returns an already-expired JWT, second login returns a fresh one
        var expiredJwt = BuildJwtWithExpiry(DateTime.UtcNow.AddMinutes(-1));
        const string freshJwt = "fresh-rep-token";
        var store = BuildStore(
            DefaultOptions(),
            call => call == 1 ? OkLoginResponse(expiredJwt) : OkLoginResponse(freshJwt),
            out var requests);
        var rep = RepIdentity();
        await store.AuthenticateAsync(rep, CancellationToken.None);

        // Act
        var token = await store.GetValidTokenAsync(rep, CancellationToken.None);

        // Assert — re-authenticated: two login calls and the fresh token returned
        Assert.Equal(freshJwt, token);
        Assert.Equal(2, requests.Count);
    }

    [Fact]
    public async Task GivenOneRepTokenExpired_WhenGetValidTokenCalled_ThenOtherIdentitiesAreNotReAuthenticated()
    {
        // Arrange — repA logs in with an expired token; repB with a long-lived token
        var expiredJwt = BuildJwtWithExpiry(DateTime.UtcNow.AddMinutes(-1));
        var validJwt = BuildJwtWithExpiry(DateTime.UtcNow.AddHours(1));
        var repA = RepIdentity(email: "rep1@dealer.com");
        var repB = RepIdentity(email: "rep2@dealer.com");
        var store = BuildStore(
            DefaultOptions(),
            call => call switch
            {
                1 => OkLoginResponse(expiredJwt),   // repA initial
                2 => OkLoginResponse(validJwt),     // repB initial
                _ => OkLoginResponse("repA-fresh")  // repA re-auth
            },
            out var requests);
        await store.AuthenticateAsync(repA, CancellationToken.None);
        await store.AuthenticateAsync(repB, CancellationToken.None);

        // Act — getting repA's token re-auths repA only; repB stays valid
        await store.GetValidTokenAsync(repA, CancellationToken.None);
        var repBToken = await store.GetValidTokenAsync(repB, CancellationToken.None);

        // Assert — repB token unchanged; total 3 login calls (repA x2, repB x1)
        Assert.Equal(validJwt, repBToken);
        Assert.Equal(3, requests.Count);
    }

    [Fact]
    public async Task GivenARepLogin_WhenAuthenticated_ThenRepIdIsParsedFromJwtSubClaim()
    {
        // Arrange
        var jwt = BuildJwtWithSubAndExpiry("rep-guid-123", DateTime.UtcNow.AddHours(1));
        var store = BuildStore(DefaultOptions(), _ => OkLoginResponse(jwt), out _);
        var rep = RepIdentity();

        // Act
        await store.AuthenticateAsync(rep, CancellationToken.None);

        // Assert
        Assert.Equal("rep-guid-123", rep.RepId);
    }

    [Fact]
    public async Task GivenBackendReturns401_WhenAuthenticateAsyncCalled_ThenThrowsAuthenticationException()
    {
        // Arrange
        var store = BuildStore(
            DefaultOptions(),
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            out _);
        var rep = RepIdentity();

        // Act & Assert
        await Assert.ThrowsAsync<AuthenticationException>(
            () => store.AuthenticateAsync(rep, CancellationToken.None));
    }

    [Fact]
    public async Task GivenValidCredentials_WhenAuthenticateAsyncCalled_ThenPostsToAuthLoginEndpointWithCredentials()
    {
        // Arrange
        var store = BuildStore(DefaultOptions(), _ => OkLoginResponse("token"), out var requests);
        var rep = RepIdentity(email: "rep3@dealer.com", password: "rep-secret");

        // Act
        await store.AuthenticateAsync(rep, CancellationToken.None);

        // Assert
        var request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/auth/login", request.RequestUri!.AbsolutePath);
        var body = await request.Content!.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("rep3@dealer.com", doc.RootElement.GetProperty("email").GetString());
        Assert.Equal("rep-secret", doc.RootElement.GetProperty("password").GetString());
    }

    // ─── JWT Test Helpers ─────────────────────────────────────────────────────

    private static string BuildJwtWithExpiry(DateTime expiry)
    {
        var header = Base64UrlEncode(JsonSerializer.Serialize(new { alg = "HS256", typ = "JWT" }));
        var unixExpiry = new DateTimeOffset(expiry).ToUnixTimeSeconds();
        var payload = Base64UrlEncode(JsonSerializer.Serialize(new { sub = "sim", exp = unixExpiry }));
        var signature = Base64UrlEncode("test-signature");
        return $"{header}.{payload}.{signature}";
    }

    private static string BuildJwtWithSubAndExpiry(string sub, DateTime expiry)
    {
        var header = Base64UrlEncode(JsonSerializer.Serialize(new { alg = "HS256", typ = "JWT" }));
        var unixExpiry = new DateTimeOffset(expiry).ToUnixTimeSeconds();
        var payload = Base64UrlEncode(JsonSerializer.Serialize(new { sub, exp = unixExpiry }));
        var signature = Base64UrlEncode("test-signature");
        return $"{header}.{payload}.{signature}";
    }

    private static string Base64UrlEncode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
