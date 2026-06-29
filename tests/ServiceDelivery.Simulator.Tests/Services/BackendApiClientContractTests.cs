using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using ServiceDelivery.Simulator.Models;
using ServiceDelivery.Simulator.Services;
using Xunit;

namespace ServiceDelivery.Simulator.Tests.Services;

// QUAL-006: captured-real-payload + fail-loud contract tests for the endpoints the
// simulator consumes. These run real backend JSON through the production
// GetFleetStateAsync / GetAvailableVehicleIdsAsync deserialization path (mocked
// HttpMessageHandler returning captured wire payloads). A wire enum that arrives as
// an integer or an unmapped name must THROW — never silently bind to a bogus value
// (see ADR-0011 / BUG-016 / BUG-036). The handler/store helpers mirror the pattern in
// BackendApiClientFleetStateTests.
public class BackendApiClientContractTests
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

    // ─── AC-1: fail loud on a drifted/integer wire enum ───────────────────────

    [Fact]
    public async Task GivenANumericRepState_WhenFleetStateParsed_ThenThrows()
    {
        // Arrange — a drifted wire payload sends repState as an integer rather than
        // the enum name. The hardened converter must refuse it, not cast it to a
        // bogus RepState.
        const string json = """
        [
          { "vehicleId": "veh-7", "claimingRepId": "rep-3", "repState": 99, "humanControlled": false, "activeRequestLocation": { "lat": 41.6, "lng": -93.6 } }
        ]
        """;
        var store = StoreWith(SimulatorIdentity());
        var (client, _) = BuildClient(store.Object, _ => JsonOk(json));

        // Act / Assert
        await Assert.ThrowsAsync<JsonException>(
            () => client.GetFleetStateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GivenAnUnrecognisedRepStateName_WhenFleetStateParsed_ThenThrows()
    {
        // Arrange — an unknown enum-name string (schema drift) must throw rather than
        // silently bind.
        const string json = """
        [
          { "vehicleId": "veh-7", "claimingRepId": "rep-3", "repState": "Frobnicated", "humanControlled": false, "activeRequestLocation": null }
        ]
        """;
        var store = StoreWith(SimulatorIdentity());
        var (client, _) = BuildClient(store.Object, _ => JsonOk(json));

        // Act / Assert
        await Assert.ThrowsAsync<JsonException>(
            () => client.GetFleetStateAsync(CancellationToken.None));
    }

    // ─── AC-2: captured-real-payload — enum-name string binds with typed fields ─

    [Fact]
    public async Task GivenARealFleetStateRow_WhenParsed_ThenRepStateStringBindsToEnumWithTypedFields()
    {
        // Arrange — a faithful backend fleet-state row (repState as the enum NAME).
        const string json = """
        [
          { "vehicleId": "veh-7", "claimingRepId": "rep-3", "repState": "EnRoute", "humanControlled": false, "activeRequestLocation": { "lat": 41.6, "lng": -93.6 } }
        ]
        """;
        var store = StoreWith(SimulatorIdentity());
        var (client, _) = BuildClient(store.Object, _ => JsonOk(json));

        // Act
        var rows = await client.GetFleetStateAsync(CancellationToken.None);

        // Assert
        var row = Assert.Single(rows);
        Assert.Equal(RepState.EnRoute, row.RepState);
        Assert.Equal("veh-7", row.VehicleId);
        Assert.Equal("rep-3", row.ClaimingRepId);
        Assert.False(row.HumanControlled);
        // The nested location is wire-faithful (lat/lng) and binds — a name drift would null it.
        Assert.NotNull(row.ActiveRequestLocation);
        Assert.Equal(41.6, row.ActiveRequestLocation!.Lat);
        Assert.Equal(-93.6, row.ActiveRequestLocation.Lng);
    }

    // ─── AC-2: captured-real-payload — object-array (BUG-016) ─────────────────

    [Fact]
    public async Task GivenARealAvailableVehiclesObjectArray_WhenParsed_ThenAllDistinctTypedVehicleIdsReturned()
    {
        // Arrange — backend GET /vehicles/available returns an ARRAY OF OBJECTS
        // (vehicleId / registration / equipment), not bare id strings. Parsing this
        // as string[] would throw; the client must project the typed vehicleId.
        const string json = """
        [
          { "vehicleId": "veh-7", "registration": "IOWA-7", "equipment": ["Hydraulic"] },
          { "vehicleId": "veh-9", "registration": "IOWA-9", "equipment": ["Electrical"] }
        ]
        """;
        var rep = Rep(token: "rep1-token");
        var store = StoreWith(SimulatorIdentity());
        var (client, _) = BuildClient(store.Object, _ => JsonOk(json));

        // Act
        var ids = await client.GetAvailableVehicleIdsAsync(rep, CancellationToken.None);

        // Assert
        Assert.Equal(new[] { "veh-7", "veh-9" }, ids);
    }
}
