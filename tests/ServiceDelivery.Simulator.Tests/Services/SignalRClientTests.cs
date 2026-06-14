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
        new SimulatorOptions { BackendBaseUrl = baseUrl };

    private static ILogger<SignalRClient> NullLogger() =>
        new Mock<ILogger<SignalRClient>>().Object;

    private static RepIdentity Rep(string repId, string token, string email) =>
        new RepIdentity
        {
            Email = email,
            Password = "pw",
            Role = IdentityRole.ServiceRep,
            RepId = repId,
            Token = token
        };

    private static JobOfferPayload Offer(string offerId) =>
        new JobOfferPayload(
            OfferId: offerId, RequestId: "req", RequesterName: "Alice",
            RequesterTier: "Gold", DtcTitle: "P0300", Latitude: 41.5, Longitude: -93.5,
            DistanceMiles: 10.0, EtaMinutes: 15);

    private static Mock<IHubConnectionFactory> WorkingFactory()
    {
        var factoryMock = new Mock<IHubConnectionFactory>();
        factoryMock
            .Setup(f => f.Build(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(() => new HubConnectionBuilder().WithUrl("http://localhost/hubs/rep").Build());
        return factoryMock;
    }

    // ─── AC-4: one Build(url, jwt) per rep with that rep's JWT ────────────────

    [Fact]
    public async Task GivenEightOperatedReps_WhenConnectAllAsyncCalled_ThenBuildIsCalledOncePerRepWithThatRepsJwt()
    {
        // Arrange
        var captured = new List<(string Url, string Jwt)>();
        var factoryMock = new Mock<IHubConnectionFactory>();
        factoryMock
            .Setup(f => f.Build(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((url, jwt) => captured.Add((url, jwt)))
            .Returns(() => new HubConnectionBuilder().WithUrl("http://localhost/hubs/rep").Build());

        var reps = Enumerable.Range(1, 8)
            .Select(i => Rep($"rep-{i}", $"token-{i}", $"rep{i}@dealer.com"))
            .ToList();

        var client = new SignalRClient(Options.Create(DefaultOptions()), factoryMock.Object, NullLogger());

        // Act
        await client.ConnectAllAsync(reps, CancellationToken.None);

        // Assert — one Build per rep, each with that rep's JWT and the rep hub URL
        Assert.Equal(8, captured.Count);
        for (var i = 1; i <= 8; i++)
        {
            Assert.Contains(captured, c => c.Jwt == $"token-{i}");
        }
        Assert.All(captured, c => Assert.Contains("/hubs/rep", c.Url));
    }

    [Fact]
    public async Task GivenAReps_WhenConnectAllAsyncCalled_ThenHubUrlIsPrefixedByBackendBaseUrl()
    {
        // Arrange
        string? capturedUrl = null;
        var factoryMock = new Mock<IHubConnectionFactory>();
        factoryMock
            .Setup(f => f.Build(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((url, _) => capturedUrl = url)
            .Returns(() => new HubConnectionBuilder().WithUrl("http://localhost/hubs/rep").Build());

        var client = new SignalRClient(
            Options.Create(DefaultOptions("https://mybackend.local")), factoryMock.Object, NullLogger());

        // Act
        await client.ConnectAllAsync(new[] { Rep("rep-1", "token-1", "rep1@dealer.com") }, CancellationToken.None);

        // Assert
        Assert.NotNull(capturedUrl);
        Assert.StartsWith("https://mybackend.local", capturedUrl);
        Assert.Contains("/hubs/rep", capturedUrl);
    }

    // ─── AC-4: per-connection handler attributes offers to the owning rep ─────

    [Fact]
    public async Task GivenAnOfferOnRep3sConnection_WhenJobOfferReceived_ThenHandlerIsInvokedWithRep3sId()
    {
        // Arrange
        var client = new SignalRClient(Options.Create(DefaultOptions()), WorkingFactory().Object, NullLogger());
        (string RepId, JobOfferPayload Payload)? received = null;
        client.RegisterJobOfferHandler((repId, payload) => received = (repId, payload));

        var reps = new[]
        {
            Rep("rep-1", "token-1", "rep1@dealer.com"),
            Rep("rep-3", "token-3", "rep3@dealer.com")
        };
        await client.ConnectAllAsync(reps, CancellationToken.None);

        // Act — simulate an offer arriving on rep-3's connection
        client.InvokeJobOfferForTest("rep-3", Offer("offer-x"));

        // Assert
        Assert.NotNull(received);
        Assert.Equal("rep-3", received!.Value.RepId);
        Assert.Equal("offer-x", received.Value.Payload.OfferId);
    }

    [Fact]
    public async Task GivenOffersOnTwoRepConnections_WhenEachReceives_ThenEachIsAttributedToItsOwningRep()
    {
        // Arrange
        var client = new SignalRClient(Options.Create(DefaultOptions()), WorkingFactory().Object, NullLogger());
        var received = new List<(string RepId, string OfferId)>();
        client.RegisterJobOfferHandler((repId, payload) => received.Add((repId, payload.OfferId)));

        var reps = new[]
        {
            Rep("rep-1", "token-1", "rep1@dealer.com"),
            Rep("rep-2", "token-2", "rep2@dealer.com")
        };
        await client.ConnectAllAsync(reps, CancellationToken.None);

        // Act
        client.InvokeJobOfferForTest("rep-1", Offer("offer-1"));
        client.InvokeJobOfferForTest("rep-2", Offer("offer-2"));

        // Assert
        Assert.Contains(("rep-1", "offer-1"), received);
        Assert.Contains(("rep-2", "offer-2"), received);
    }

    // ─── Reconnect policy (unchanged) ─────────────────────────────────────────

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

        // Act
        var intervals = factory.ReconnectIntervals;

        // Assert
        Assert.Equal(expectedIntervals, intervals);
    }

    // ─── Per-connection failure isolation: one rep failing is logged and skipped ─

    [Fact]
    public async Task GivenOneRepConnectionThrows_WhenConnectAllAsyncCalled_ThenErrorIsLoggedAndOtherRepsStillConnect()
    {
        // Arrange — rep-1 connects to an unreachable host (StartAsync throws); rep-2 connects fine
        var loggerMock = new Mock<ILogger<SignalRClient>>();
        var factoryMock = new Mock<IHubConnectionFactory>();
        factoryMock
            .Setup(f => f.Build(It.IsAny<string>(), "token-bad"))
            .Returns(() => new HubConnectionBuilder()
                .WithUrl("http://unreachable-host-that-does-not-exist.invalid/hubs/rep")
                .Build());
        factoryMock
            .Setup(f => f.Build(It.IsAny<string>(), "token-good"))
            .Returns(() => new HubConnectionBuilder().WithUrl("http://localhost/hubs/rep").Build());

        var reps = new[]
        {
            Rep("rep-bad", "token-bad", "rep1@dealer.com"),
            Rep("rep-good", "token-good", "rep2@dealer.com")
        };

        var client = new SignalRClient(Options.Create(DefaultOptions()), factoryMock.Object, loggerMock.Object);

        // Act
        var exception = await Record.ExceptionAsync(
            () => client.ConnectAllAsync(reps, CancellationToken.None));

        // Assert — no exception propagated; an error was logged for the failing connection
        Assert.Null(exception);
        loggerMock.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }
}
