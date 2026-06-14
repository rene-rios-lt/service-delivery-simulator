using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using ServiceDelivery.Simulator.Models;
using ServiceDelivery.Simulator.Services;
using Xunit;

namespace ServiceDelivery.Simulator.Tests.Services;

// Covers the BackendApiClient HTTP behaviour after the per-rep retrofit:
// position posts carry the Simulator identity's token; accept/decline carry the
// responding rep's token; URLs/methods are correct; position 401 re-auths the
// Simulator identity and retries once. Authentication/expiry/login itself is
// tested in IdentitySessionStoreTests — the store owns that responsibility.
public class BackendApiClientAuthTests
{
    private const string BaseUrl = "https://backend.local";

    // ─── Helpers ───────────────────────────────────────────────────────────────

    private static RepIdentity SimulatorIdentity(string token = "sim-token") =>
        new RepIdentity
        {
            Email = "sim@system.internal",
            Password = "pw",
            Role = IdentityRole.Simulator,
            Token = token
        };

    private static RepIdentity RepIdentity(string repId, string token, string email = "rep1@dealer.com") =>
        new RepIdentity
        {
            Email = email,
            Password = "pw",
            Role = IdentityRole.ServiceRep,
            RepId = repId,
            Token = token
        };

    private static (BackendApiClient client, Mock<HttpMessageHandler> handlerMock, List<HttpRequestMessage> requests)
        BuildClient(IIdentitySessionStore store, Func<int, HttpResponseMessage> responder, ILogger<BackendApiClient>? logger = null)
    {
        var requests = new List<HttpRequestMessage>();
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

        var httpClient = new HttpClient(handlerMock.Object) { BaseAddress = new Uri(BaseUrl) };
        var client = new BackendApiClient(httpClient, store, logger ?? NullLogger<BackendApiClient>.Instance);
        return (client, handlerMock, requests);
    }

    private static Mock<IIdentitySessionStore> StoreWith(RepIdentity simulator)
    {
        var store = new Mock<IIdentitySessionStore>();
        store.Setup(s => s.Simulator).Returns(simulator);
        store.Setup(s => s.GetValidTokenAsync(It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((RepIdentity id, CancellationToken _) => id.Token!);
        return store;
    }

    private static HttpResponseMessage Ok() => new HttpResponseMessage(HttpStatusCode.OK);

    // ─── AC-6: Position posting uses the Simulator token ──────────────────────

    [Fact]
    public async Task GivenStoredSimulatorToken_WhenPostPositionAsyncCalled_ThenAuthorizationHeaderIsSimulatorToken()
    {
        // Arrange
        var simulator = SimulatorIdentity(token: "the-simulator-token");
        var store = StoreWith(simulator);
        var (client, _, requests) = BuildClient(store.Object, _ => Ok());
        var position = new VehiclePosition("vehicle-1", 41.5, -93.6);

        // Act
        await client.PostPositionAsync(position, CancellationToken.None);

        // Assert
        var request = Assert.Single(requests);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("the-simulator-token", request.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task GivenStoredSimulatorToken_WhenPostPositionAsyncCalled_ThenPostsToVehiclesPositionEndpoint()
    {
        // Arrange
        var store = StoreWith(SimulatorIdentity());
        var (client, _, requests) = BuildClient(store.Object, _ => Ok());
        var position = new VehiclePosition("vehicle-7", 41.5, -93.6);

        // Act
        await client.PostPositionAsync(position, CancellationToken.None);

        // Assert
        var request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/vehicles/vehicle-7/position", request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GivenPositionPostReturns401_WhenPostPositionAsyncCalled_ThenSimulatorIdentityReAuthenticatesAndRetriesOnce()
    {
        // Arrange — first POST 401, store re-auths the Simulator identity, retry succeeds
        var simulator = SimulatorIdentity();
        var store = new Mock<IIdentitySessionStore>();
        store.Setup(s => s.Simulator).Returns(simulator);
        store.Setup(s => s.GetValidTokenAsync(simulator, It.IsAny<CancellationToken>()))
            .ReturnsAsync("sim-token");

        var (client, _, requests) = BuildClient(
            store.Object,
            call => call == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : Ok());
        var position = new VehiclePosition("vehicle-1", 41.5, -93.6);

        // Act
        await client.PostPositionAsync(position, CancellationToken.None);

        // Assert — two POSTs (original + retry); the Simulator identity was re-authenticated
        Assert.Equal(2, requests.Count);
        store.Verify(s => s.AuthenticateAsync(simulator, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GivenPositionPostReturns401Twice_WhenPostPositionAsyncCalled_ThenLogsErrorAndDoesNotThrow()
    {
        // Arrange — both the original and the retry POST return 401
        var simulator = SimulatorIdentity();
        var store = new Mock<IIdentitySessionStore>();
        store.Setup(s => s.Simulator).Returns(simulator);
        store.Setup(s => s.GetValidTokenAsync(simulator, It.IsAny<CancellationToken>()))
            .ReturnsAsync("sim-token");
        var loggerMock = new Mock<ILogger<BackendApiClient>>();

        var (client, _, _) = BuildClient(
            store.Object,
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            loggerMock.Object);
        var position = new VehiclePosition("vehicle-1", 41.5, -93.6);

        // Act
        var exception = await Record.ExceptionAsync(
            () => client.PostPositionAsync(position, CancellationToken.None));

        // Assert
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

    // ─── AC-3 / AC-5: Accept/Decline carry the responding rep's token ─────────

    [Fact]
    public async Task GivenARepIdentity_WhenAcceptJobOfferAsyncCalled_ThenAuthorizationHeaderIsThatRepsToken()
    {
        // Arrange
        var rep = RepIdentity(repId: "rep-3", token: "rep3-token", email: "rep3@dealer.com");
        var store = StoreWith(SimulatorIdentity());
        var (client, _, requests) = BuildClient(store.Object, _ => Ok());

        // Act
        await client.AcceptJobOfferAsync("offer-1", rep, CancellationToken.None);

        // Assert
        var request = Assert.Single(requests);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("rep3-token", request.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task GivenTwoDifferentReps_WhenEachAcceptsAnOffer_ThenEachRequestCarriesItsOwnRepToken()
    {
        // Arrange
        var repA = RepIdentity(repId: "rep-1", token: "tokenA", email: "rep1@dealer.com");
        var repB = RepIdentity(repId: "rep-2", token: "tokenB", email: "rep2@dealer.com");
        var store = StoreWith(SimulatorIdentity());
        var (client, _, requests) = BuildClient(store.Object, _ => Ok());

        // Act
        await client.AcceptJobOfferAsync("offer-1", repA, CancellationToken.None);
        await client.AcceptJobOfferAsync("offer-2", repB, CancellationToken.None);

        // Assert
        Assert.Equal(2, requests.Count);
        Assert.Equal("tokenA", requests[0].Headers.Authorization?.Parameter);
        Assert.Equal("tokenB", requests[1].Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task GivenAnOfferAndRep_WhenAcceptJobOfferAsyncCalled_ThenPostsToJobOffersAcceptEndpoint()
    {
        // Arrange
        var rep = RepIdentity(repId: "rep-1", token: "token");
        var store = StoreWith(SimulatorIdentity());
        var (client, _, requests) = BuildClient(store.Object, _ => Ok());

        // Act
        await client.AcceptJobOfferAsync("offer-42", rep, CancellationToken.None);

        // Assert
        var request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/job-offers/offer-42/accept", request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GivenAnOfferAndRep_WhenDeclineJobOfferAsyncCalled_ThenPostsToJobOffersDeclineEndpoint()
    {
        // Arrange
        var rep = RepIdentity(repId: "rep-1", token: "token");
        var store = StoreWith(SimulatorIdentity());
        var (client, _, requests) = BuildClient(store.Object, _ => Ok());

        // Act
        await client.DeclineJobOfferAsync("offer-42", rep, CancellationToken.None);

        // Assert
        var request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/job-offers/offer-42/decline", request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GivenAnOfferAndRep_WhenDeclineJobOfferAsyncCalled_ThenAuthorizationHeaderIsThatRepsToken()
    {
        // Arrange
        var rep = RepIdentity(repId: "rep-5", token: "rep5-token", email: "rep5@dealer.com");
        var store = StoreWith(SimulatorIdentity());
        var (client, _, requests) = BuildClient(store.Object, _ => Ok());

        // Act
        await client.DeclineJobOfferAsync("offer-9", rep, CancellationToken.None);

        // Assert
        var request = Assert.Single(requests);
        Assert.Equal("rep5-token", request.Headers.Authorization?.Parameter);
    }
}
