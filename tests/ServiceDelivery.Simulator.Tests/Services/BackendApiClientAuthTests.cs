using System.Net;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using ServiceDelivery.Simulator.Configuration;
using ServiceDelivery.Simulator.Models;
using ServiceDelivery.Simulator.Services;
using Xunit;

namespace ServiceDelivery.Simulator.Tests.Services;

public class BackendApiClientAuthTests
{
    // ─── Helpers ───────────────────────────────────────────────────────────────

    private static SimulatorOptions DefaultOptions(
        string email = "sim@test.internal",
        string password = "secret",
        string baseUrl = "https://backend.local") =>
        new SimulatorOptions
        {
            SimulatorEmail = email,
            SimulatorPassword = password,
            BackendBaseUrl = baseUrl
        };

    private static (BackendApiClient client, Mock<HttpMessageHandler> handlerMock)
        BuildClient(
            SimulatorOptions options,
            HttpResponseMessage? loginResponse = null,
            HttpResponseMessage? secondResponse = null)
    {
        loginResponse ??= OkLoginResponse("test-jwt");

        var handlerMock = new Mock<HttpMessageHandler>();

        if (secondResponse is not null)
        {
            handlerMock
                .Protected()
                .SetupSequence<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(loginResponse)
                .ReturnsAsync(secondResponse);
        }
        else
        {
            handlerMock
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(loginResponse);
        }

        var httpClient = new HttpClient(handlerMock.Object);
        var client = new BackendApiClient(httpClient, Options.Create(options), NullLogger<BackendApiClient>.Instance);
        return (client, handlerMock);
    }

    private static HttpResponseMessage OkLoginResponse(string token) =>
        new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { token }),
                Encoding.UTF8,
                "application/json")
        };

    private static HttpResponseMessage OkPositionResponse() =>
        new HttpResponseMessage(HttpStatusCode.OK);

    // ─── AC-1: POST /auth/login with simulator credentials ────────────────────

    [Fact]
    public async Task GivenValidSimulatorCredentials_WhenAuthenticateAsyncCalled_ThenPostsToAuthLoginEndpoint()
    {
        // Arrange
        var options = DefaultOptions(email: "sim@test.internal", password: "secret");
        var (client, handlerMock) = BuildClient(options);
        HttpRequestMessage? capturedRequest = null;
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(OkLoginResponse("jwt-token"));

        // Act
        await client.AuthenticateAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("/auth/login", capturedRequest.RequestUri!.AbsolutePath);

        var body = await capturedRequest.Content!.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("sim@test.internal", doc.RootElement.GetProperty("email").GetString());
        Assert.Equal("secret", doc.RootElement.GetProperty("password").GetString());
    }

    // ─── AC-2: JWT stored and set as Authorization: Bearer ───────────────────

    [Fact]
    public async Task GivenSuccessfulAuthentication_WhenAuthenticateAsyncCalled_ThenJwtIsStored()
    {
        // Arrange
        const string expectedJwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.test";
        var (client, _) = BuildClient(DefaultOptions(), loginResponse: OkLoginResponse(expectedJwt));

        // Act
        await client.AuthenticateAsync(CancellationToken.None);

        // Assert
        Assert.Equal(expectedJwt, client.StoredJwt);
    }

    [Fact]
    public async Task GivenStoredJwt_WhenPostPositionAsyncCalled_ThenAuthorizationHeaderIsBearerToken()
    {
        // Arrange
        const string expectedJwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.test";
        var (client, handlerMock) = BuildClient(
            DefaultOptions(),
            loginResponse: OkLoginResponse(expectedJwt),
            secondResponse: OkPositionResponse());

        await client.AuthenticateAsync(CancellationToken.None);

        HttpRequestMessage? capturedPositionRequest = null;
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedPositionRequest = req)
            .ReturnsAsync(OkPositionResponse());

        var position = new VehiclePosition("vehicle-1", 41.5, -93.6);

        // Act
        await client.PostPositionAsync(position, CancellationToken.None);

        // Assert
        Assert.NotNull(capturedPositionRequest);
        Assert.Equal("Bearer", capturedPositionRequest!.Headers.Authorization?.Scheme);
        Assert.Equal(expectedJwt, capturedPositionRequest.Headers.Authorization?.Parameter);
    }

    // ─── AC-3: Authentication failure → AuthenticationException ──────────────

    [Fact]
    public async Task GivenBackendReturns401_WhenAuthenticateAsyncCalled_ThenThrowsAuthenticationException()
    {
        // Arrange
        var unauthorizedResponse = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var (client, _) = BuildClient(DefaultOptions(), loginResponse: unauthorizedResponse);

        // Act & Assert
        await Assert.ThrowsAsync<AuthenticationException>(
            () => client.AuthenticateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GivenAuthenticationException_WhenStartupRuns_ThenHostLogsErrorAndStops()
    {
        // Arrange
        var unauthorizedResponse = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var (client, _) = BuildClient(DefaultOptions(), loginResponse: unauthorizedResponse);

        // Act
        var exception = await Record.ExceptionAsync(
            () => client.AuthenticateAsync(CancellationToken.None));

        // Assert
        Assert.NotNull(exception);
        Assert.IsType<AuthenticationException>(exception);
        Assert.Contains("Unauthorized", exception.Message);
    }

    // ─── AC-4: Re-authentication on JWT expiry ────────────────────────────────

    [Fact]
    public async Task GivenExpiredJwt_WhenPostPositionAsyncCalled_ThenReAuthenticatesFirst()
    {
        // Arrange — build an already-expired JWT token (exp in the past)
        var expiredJwt = BuildJwtWithExpiry(DateTime.UtcNow.AddMinutes(-1));
        var freshJwt = "fresh-jwt-token";

        var handlerMock = new Mock<HttpMessageHandler>();
        var callCount = 0;
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount == 1
                    ? OkLoginResponse(expiredJwt)   // initial auth returns expired JWT
                    : callCount == 2
                        ? OkLoginResponse(freshJwt) // re-auth returns fresh JWT
                        : OkPositionResponse();     // position POST succeeds
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var client = new BackendApiClient(httpClient, Options.Create(DefaultOptions()), NullLogger<BackendApiClient>.Instance);

        // Perform initial authentication (stores expired JWT)
        await client.AuthenticateAsync(CancellationToken.None);
        var position = new VehiclePosition("vehicle-1", 41.5, -93.6);

        // Act — posting position should detect expiry and re-authenticate first
        await client.PostPositionAsync(position, CancellationToken.None);

        // Assert — three calls: initial auth, re-auth, position POST
        handlerMock.Protected().Verify(
            "SendAsync",
            Times.Exactly(3),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
        Assert.Equal(freshJwt, client.StoredJwt);
    }

    [Fact]
    public async Task GivenReAuthenticationSucceeds_WhenOriginalCallRetried_ThenCallSucceeds()
    {
        // Arrange — build an already-expired JWT token
        var expiredJwt = BuildJwtWithExpiry(DateTime.UtcNow.AddMinutes(-1));
        var freshJwt = "fresh-valid-jwt";

        var handlerMock = new Mock<HttpMessageHandler>();
        var callCount = 0;
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount == 1
                    ? OkLoginResponse(expiredJwt)   // initial auth
                    : callCount == 2
                        ? OkLoginResponse(freshJwt) // re-auth
                        : OkPositionResponse();     // position POST
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var client = new BackendApiClient(httpClient, Options.Create(DefaultOptions()), NullLogger<BackendApiClient>.Instance);
        await client.AuthenticateAsync(CancellationToken.None);

        var position = new VehiclePosition("vehicle-1", 41.5, -93.6);

        // Act
        var exception = await Record.ExceptionAsync(
            () => client.PostPositionAsync(position, CancellationToken.None));

        // Assert — no exception thrown, call completed successfully
        Assert.Null(exception);
    }

    // ─── AC-3 (SIM-004): 401 on position POST → re-authenticate and retry once ─

    [Fact]
    public async Task GivenBackendReturns401OnPositionPost_WhenPostPositionAsyncCalled_ThenReAuthenticatesAndRetries()
    {
        // Arrange — initial auth succeeds, position POST returns 401, re-auth succeeds, retry succeeds
        var options = DefaultOptions();
        var handlerMock = new Mock<HttpMessageHandler>();
        var callCount = 0;

        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount switch
                {
                    1 => OkLoginResponse("initial-jwt"),          // initial EnsureAuth
                    2 => new HttpResponseMessage(HttpStatusCode.Unauthorized), // position POST → 401
                    3 => OkLoginResponse("refreshed-jwt"),        // re-auth call
                    4 => OkPositionResponse(),                    // retry POST succeeds
                    _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
                };
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var client = new BackendApiClient(httpClient, Options.Create(options), NullLogger<BackendApiClient>.Instance);
        var position = new VehiclePosition("vehicle-1", 41.5, -93.6);

        // Act
        var exception = await Record.ExceptionAsync(
            () => client.PostPositionAsync(position, CancellationToken.None));

        // Assert — no exception thrown; 4 HTTP calls made (initial auth + POST 401 + re-auth + retry POST)
        Assert.Null(exception);
        Assert.Equal(4, callCount);
    }

    [Fact]
    public async Task GivenBackendReturns401TwiceOnPositionPost_WhenPostPositionAsyncCalled_ThenLogsErrorAndDoesNotThrow()
    {
        // Arrange — initial auth succeeds, position POST returns 401, re-auth succeeds, retry also returns 401
        var options = DefaultOptions();
        var handlerMock = new Mock<HttpMessageHandler>();
        var loggerMock = new Mock<ILogger<BackendApiClient>>();
        var callCount = 0;

        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount switch
                {
                    1 => OkLoginResponse("initial-jwt"),          // initial EnsureAuth
                    2 => new HttpResponseMessage(HttpStatusCode.Unauthorized), // first POST → 401
                    3 => OkLoginResponse("refreshed-jwt"),        // re-auth call
                    4 => new HttpResponseMessage(HttpStatusCode.Unauthorized), // retry POST → 401 again
                    _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
                };
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var client = new BackendApiClient(httpClient, Options.Create(options), loggerMock.Object);
        var position = new VehiclePosition("vehicle-1", 41.5, -93.6);

        // Act
        var exception = await Record.ExceptionAsync(
            () => client.PostPositionAsync(position, CancellationToken.None));

        // Assert — no exception thrown; error was logged
        Assert.Null(exception);
        loggerMock.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("position")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
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

    private static string Base64UrlEncode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
