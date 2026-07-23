using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using ServiceDelivery.Simulator.Models;
using ServiceDelivery.Simulator.Services;
using Xunit;

namespace ServiceDelivery.Simulator.Tests.Services;

// Covers the SIM-008 additions to BackendApiClient: the Simulator-token fleet-state
// read (incl. JSON parse and null active location) and the rep-token claim/available
// operations. Authentication/expiry itself is owned by IdentitySessionStoreTests.
public class BackendApiClientFleetStateTests
{
    private const string BaseUrl = "https://backend.local";

    private static RepIdentity SimulatorIdentity(string token = "sim-token") =>
        new()
        {
            Email = "sim@system.internal",
            Password = "pw",
            Role = IdentityRole.Simulator,
            Token = token
        };

    private static RepIdentity Rep(string token, string email = "rep1@dealer.com") =>
        new()
        {
            Email = email,
            Password = "pw",
            Role = IdentityRole.ServiceRep,
            RepId = "rep-1",
            Token = token
        };

    private static Mock<IIdentitySessionStore> StoreWith(RepIdentity simulator)
    {
        var store = new Mock<IIdentitySessionStore>();
        store.Setup(s => s.Simulator).Returns(simulator);
        store.Setup(s => s.GetValidTokenAsync(It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((RepIdentity id, CancellationToken _) => id.Token!);
        return store;
    }

    private static (BackendApiClient client, List<HttpRequestMessage> requests) BuildClient(
        IIdentitySessionStore store, Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var requests = new List<HttpRequestMessage>();
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => requests.Add(req))
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) => responder(req));

        var httpClient = new HttpClient(handlerMock.Object) { BaseAddress = new Uri(BaseUrl) };
        var client = new BackendApiClient(httpClient, store, NullLogger<BackendApiClient>.Instance);
        return (client, requests);
    }

    private static HttpResponseMessage JsonOk(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };

    // ─── AC-1: fleet-state read uses the Simulator token ──────────────────────

    [Fact]
    public async Task GivenAFleetStateResponse_WhenGetFleetStateAsyncCalled_ThenSimulatorTokenIsUsed()
    {
        // Arrange
        var store = StoreWith(SimulatorIdentity(token: "the-sim-token"));
        var (client, requests) = BuildClient(store.Object, _ => JsonOk("[]"));

        // Act
        await client.GetFleetStateAsync(CancellationToken.None);

        // Assert
        var request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/simulator/fleet-state", request.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("the-sim-token", request.Headers.Authorization?.Parameter);
    }

    // ─── AC-1: response parses into per-vehicle rows incl. null active location ─

    [Fact]
    public async Task GivenAFleetStateRowWithNullActiveLocation_WhenParsed_ThenActiveRequestLocationIsNull()
    {
        // Arrange
        const string json = """
        [
          { "vehicleId": "V-001", "claimingRepId": "rep-1", "repState": "Available", "humanControlled": false, "activeRequestLocation": null }
        ]
        """;
        var store = StoreWith(SimulatorIdentity());
        var (client, _) = BuildClient(store.Object, _ => JsonOk(json));

        // Act
        var rows = await client.GetFleetStateAsync(CancellationToken.None);

        // Assert
        var row = Assert.Single(rows);
        Assert.Equal("V-001", row.VehicleId);
        Assert.Equal("rep-1", row.ClaimingRepId);
        Assert.Equal(RepState.Available, row.RepState);
        Assert.Null(row.ActiveRequestLocation);
    }

    [Fact]
    public async Task GivenAFleetStateRowMarkedHumanControlled_WhenParsed_ThenHumanControlledIsTrue()
    {
        // Arrange
        const string json = """
        [
          { "vehicleId": "V-002", "claimingRepId": "rep-2", "repState": "EnRoute", "humanControlled": true, "activeRequestLocation": { "lat": 41.6, "lng": -93.7 } }
        ]
        """;
        var store = StoreWith(SimulatorIdentity());
        var (client, _) = BuildClient(store.Object, _ => JsonOk(json));

        // Act
        var rows = await client.GetFleetStateAsync(CancellationToken.None);

        // Assert
        var row = Assert.Single(rows);
        Assert.True(row.HumanControlled);
        Assert.Equal(RepState.EnRoute, row.RepState);
        Assert.NotNull(row.ActiveRequestLocation);
        Assert.Equal(41.6, row.ActiveRequestLocation!.Lat);
        Assert.Equal(-93.7, row.ActiveRequestLocation.Lng);
    }

    // ─── AC-4: claim targets /vehicles/{id}/claim with the rep token ──────────

    [Fact]
    public async Task GivenARepAndVehicle_WhenClaimVehicleAsyncCalled_ThenClaimEndpointIsCalledWithRepToken()
    {
        // Arrange
        var rep = Rep(token: "rep1-token");
        var store = StoreWith(SimulatorIdentity());
        var (client, requests) = BuildClient(store.Object, _ => new HttpResponseMessage(HttpStatusCode.OK));

        // Act
        var outcome = await client.ClaimVehicleAsync("V-005", rep, CancellationToken.None);

        // Assert
        var request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/vehicles/V-005/claim", request.RequestUri!.AbsolutePath);
        Assert.Equal("rep1-token", request.Headers.Authorization?.Parameter);
        Assert.Equal(ClaimOutcome.Claimed, outcome);
    }

    // ─── BUG-052: claim maps the HTTP status to a ClaimOutcome so the coordinator can
    // tell a genuine 409 conflict (vehicle held by another rep — exclude it) apart from
    // a transient failure (5xx/network — leave it eligible for a later attempt) ───────
    [Theory]
    [InlineData(HttpStatusCode.OK, ClaimOutcome.Claimed)]
    [InlineData(HttpStatusCode.NoContent, ClaimOutcome.Claimed)]
    [InlineData(HttpStatusCode.Conflict, ClaimOutcome.Conflict)]
    [InlineData(HttpStatusCode.InternalServerError, ClaimOutcome.Failed)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ClaimOutcome.Failed)]
    public async Task GivenAClaimResponseStatus_WhenClaimVehicleAsyncCalled_ThenOutcomeReflectsTheStatus(
        HttpStatusCode status, ClaimOutcome expected)
    {
        // Arrange
        var rep = Rep(token: "rep1-token");
        var store = StoreWith(SimulatorIdentity());
        var (client, _) = BuildClient(store.Object, _ => new HttpResponseMessage(status));

        // Act
        var outcome = await client.ClaimVehicleAsync("V-005", rep, CancellationToken.None);

        // Assert
        Assert.Equal(expected, outcome);
    }

    // ─── QUAL-029 AC-2: accept maps the HTTP status to an AcceptOutcome so the decision
    // engine can tell a genuine 409 conflict (rep already busy / offer no longer Pending
    // — decline immediately) apart from a transient failure (5xx — take no action),
    // mirroring the ClaimVehicleAsync/ClaimOutcome theory above ───────────────────────
    [Theory]
    [InlineData(HttpStatusCode.OK, AcceptOutcome.Accepted)]
    [InlineData(HttpStatusCode.NoContent, AcceptOutcome.Accepted)]
    [InlineData(HttpStatusCode.Conflict, AcceptOutcome.Conflict)]
    [InlineData(HttpStatusCode.InternalServerError, AcceptOutcome.Failed)]
    [InlineData(HttpStatusCode.ServiceUnavailable, AcceptOutcome.Failed)]
    public async Task GivenAnAcceptResponseStatus_WhenAcceptJobOfferAsyncCalled_ThenOutcomeReflectsTheStatus(
        HttpStatusCode status, AcceptOutcome expected)
    {
        // Arrange
        var rep = Rep(token: "rep1-token");
        var store = StoreWith(SimulatorIdentity());
        var (client, _) = BuildClient(store.Object, _ => new HttpResponseMessage(status));

        // Act
        var outcome = await client.AcceptJobOfferAsync("offer-1", rep, CancellationToken.None);

        // Assert
        Assert.Equal(expected, outcome);
    }

    [Fact]
    public async Task GivenAvailableVehiclesAsObjects_WhenGetAvailableVehicleIdsAsyncCalled_ThenRepTokenIsUsedAndProjectedIdsReturned()
    {
        // Arrange — backend GET /vehicles/available returns an array of objects
        // ({ vehicleId, registration, equipment }), not bare id strings.
        const string json = """
        [
          { "vehicleId": "V-003", "registration": "REG-003", "equipment": ["tow"] },
          { "vehicleId": "V-004", "registration": "REG-004", "equipment": [] }
        ]
        """;
        var rep = Rep(token: "rep1-token");
        var store = StoreWith(SimulatorIdentity());
        var (client, requests) = BuildClient(store.Object, _ => JsonOk(json));

        // Act
        var ids = await client.GetAvailableVehicleIdsAsync(rep, CancellationToken.None);

        // Assert
        var request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/vehicles/available", request.RequestUri!.AbsolutePath);
        Assert.Equal("rep1-token", request.Headers.Authorization?.Parameter);
        Assert.Equal(new[] { "V-003", "V-004" }, ids);
    }
}
