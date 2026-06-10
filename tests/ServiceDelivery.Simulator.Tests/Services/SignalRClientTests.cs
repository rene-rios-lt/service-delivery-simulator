using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using ServiceDelivery.Simulator.Configuration;
using ServiceDelivery.Simulator.Models;
using ServiceDelivery.Simulator.Services;
using Xunit;

namespace ServiceDelivery.Simulator.Tests.Services;

public class SignalRClientTests
{
    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static SimulatorOptions DefaultOptions(string baseUrl = "https://backend.local") =>
        new SimulatorOptions
        {
            BackendBaseUrl = baseUrl,
            SimulatorEmail = "sim@test.internal",
            SimulatorPassword = "secret"
        };

    private static ILogger<SignalRClient> NullLogger() =>
        new Mock<ILogger<SignalRClient>>().Object;

    // ─── AC-1: HubConnection URL contains /hubs/rep ───────────────────────────

    [Fact]
    public async Task GivenAJwtAndBaseUrl_WhenConnectAsyncCalled_ThenHubConnectionUrlContainsHubsRep()
    {
        // Arrange
        string? capturedUrl = null;
        var factoryMock = new Mock<IHubConnectionFactory>();
        factoryMock
            .Setup(f => f.Build(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((url, _) => capturedUrl = url)
            .Returns(new HubConnectionBuilder().WithUrl("http://localhost/hubs/rep").Build());

        var client = new SignalRClient(
            Options.Create(DefaultOptions("https://backend.local")),
            factoryMock.Object,
            NullLogger());

        // Act
        await client.ConnectAsync("test-jwt", CancellationToken.None);

        // Assert
        Assert.NotNull(capturedUrl);
        Assert.Contains("/hubs/rep", capturedUrl);
    }

    [Fact]
    public async Task GivenAJwtAndBaseUrl_WhenConnectAsyncCalled_ThenBaseUrlPrefixesHubsRepPath()
    {
        // Arrange
        string? capturedUrl = null;
        var factoryMock = new Mock<IHubConnectionFactory>();
        factoryMock
            .Setup(f => f.Build(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((url, _) => capturedUrl = url)
            .Returns(new HubConnectionBuilder().WithUrl("http://localhost/hubs/rep").Build());

        var client = new SignalRClient(
            Options.Create(DefaultOptions("https://mybackend.local")),
            factoryMock.Object,
            NullLogger());

        // Act
        await client.ConnectAsync("test-jwt", CancellationToken.None);

        // Assert
        Assert.NotNull(capturedUrl);
        Assert.StartsWith("https://mybackend.local", capturedUrl);
    }

    [Fact]
    public async Task GivenAJwtAndBaseUrl_WhenConnectAsyncCalled_ThenBearerTokenIsPassedToHubConnection()
    {
        // Arrange
        const string expectedJwt = "eyJhbGciOiJIUzI1NiJ9.test";
        string? capturedJwt = null;
        var factoryMock = new Mock<IHubConnectionFactory>();
        factoryMock
            .Setup(f => f.Build(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((_, jwt) => capturedJwt = jwt)
            .Returns(new HubConnectionBuilder().WithUrl("http://localhost/hubs/rep").Build());

        var client = new SignalRClient(
            Options.Create(DefaultOptions()),
            factoryMock.Object,
            NullLogger());

        // Act
        await client.ConnectAsync(expectedJwt, CancellationToken.None);

        // Assert
        Assert.Equal(expectedJwt, capturedJwt);
    }

    // ─── AC-2: Handler registered for "JobOfferReceived" ─────────────────────

    [Fact]
    public async Task GivenARegisteredHandler_WhenConnectAsyncCalled_ThenHandlerIsStoredForJobOfferReceived()
    {
        // Arrange
        var factoryMock = new Mock<IHubConnectionFactory>();
        factoryMock
            .Setup(f => f.Build(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new HubConnectionBuilder().WithUrl("http://localhost/hubs/rep").Build());

        var client = new SignalRClient(
            Options.Create(DefaultOptions()),
            factoryMock.Object,
            NullLogger());

        JobOfferPayload? receivedPayload = null;
        var expectedPayload = new JobOfferPayload(
            OfferId: "offer-1", RequestId: "req-1", RequesterName: "Alice",
            RequesterTier: "Gold", DtcTitle: "P0300", Latitude: 41.5, Longitude: -93.5,
            DistanceMiles: 10.0, EtaMinutes: 15);
        client.RegisterJobOfferHandler(p => receivedPayload = p);

        // Act
        await client.ConnectAsync("test-jwt", CancellationToken.None);

        // Assert — handler is stored and, when invoked, receives the payload
        Assert.NotNull(client.JobOfferHandlerForTest);
        client.JobOfferHandlerForTest!(expectedPayload);
        Assert.Equal(expectedPayload.OfferId, receivedPayload!.OfferId);
    }

    // ─── AC-3: Exponential back-off reconnect policy configured ──────────────

    [Fact]
    public void GivenADefaultHubConnectionFactory_WhenBuilt_ThenReconnectIntervalsAreExponentialBackoff()
    {
        // Arrange
        var factory = new DefaultHubConnectionFactory();
        var expectedIntervals = new[]
        {
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30)
        };

        // Act — build a connection; we verify factory exposes the configured intervals
        var intervals = factory.ReconnectIntervals;

        // Assert
        Assert.Equal(expectedIntervals, intervals);
    }

    // ─── AC-4: Connection failure logged; no exception propagates ─────────────

    [Fact]
    public async Task GivenConnectAsyncThrows_WhenSignalRClientConnects_ThenExceptionIsLoggedAndNotRethrown()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<SignalRClient>>();
        var factoryMock = new Mock<IHubConnectionFactory>();

        // Build a connection to an unreachable endpoint so StartAsync will throw
        factoryMock
            .Setup(f => f.Build(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new HubConnectionBuilder()
                .WithUrl("http://unreachable-host-that-does-not-exist.invalid/hubs/rep")
                .Build());

        var client = new SignalRClient(
            Options.Create(DefaultOptions()),
            factoryMock.Object,
            loggerMock.Object);

        // Act
        var exception = await Record.ExceptionAsync(
            () => client.ConnectAsync("test-jwt", CancellationToken.None));

        // Assert — no exception propagates
        Assert.Null(exception);
    }

    [Fact]
    public async Task GivenConnectAsyncThrows_WhenSignalRClientConnects_ThenErrorIsLogged()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<SignalRClient>>();
        var factoryMock = new Mock<IHubConnectionFactory>();

        factoryMock
            .Setup(f => f.Build(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new HubConnectionBuilder()
                .WithUrl("http://unreachable-host-that-does-not-exist.invalid/hubs/rep")
                .Build());

        var client = new SignalRClient(
            Options.Create(DefaultOptions()),
            factoryMock.Object,
            loggerMock.Object);

        // Act
        await client.ConnectAsync("test-jwt", CancellationToken.None);

        // Assert — an error was logged
        loggerMock.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
