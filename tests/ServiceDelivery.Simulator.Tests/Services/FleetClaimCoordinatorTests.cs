using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using ServiceDelivery.Simulator.Models;
using ServiceDelivery.Simulator.Services;
using Xunit;

namespace ServiceDelivery.Simulator.Tests.Services;

// AC-4: at startup each automated rep claims one free vehicle; during a tick the
// coordinator rebalances by claiming a free vehicle for any operated rep that holds
// none, and makes no claim when every operated rep already holds a vehicle.
public class FleetClaimCoordinatorTests
{
    private static RepIdentity Rep(string repId) =>
        new()
        {
            Email = $"{repId}@dealer.com",
            Password = "pw",
            Role = IdentityRole.ServiceRep,
            RepId = repId
        };

    private static FleetStateRow Row(string vehicleId, string? claimingRepId, bool humanControlled = false) =>
        new(vehicleId, claimingRepId, RepState.Available, humanControlled, ActiveRequestLocation: null);

    private static Mock<IIdentitySessionStore> StoreWith(params RepIdentity[] reps)
    {
        var store = new Mock<IIdentitySessionStore>();
        store.Setup(s => s.Reps).Returns(reps);
        return store;
    }

    private static FleetClaimCoordinator Build(
        Mock<IIdentitySessionStore> store, Mock<IBackendApiClient> apiClient, IYieldedRepRegistry? registry = null) =>
        new(store.Object, apiClient.Object, registry ?? new YieldedRepRegistry(),
            NullLogger<FleetClaimCoordinator>.Instance);

    [Fact]
    public async Task GivenAutomatedReps_WhenClaimInitialVehiclesAsyncRuns_ThenEachRepClaimsOneFreeVehicle()
    {
        // Arrange
        var rep1 = Rep("rep-1");
        var rep2 = Rep("rep-2");
        var store = StoreWith(rep1, rep2);

        var apiClient = new Mock<IBackendApiClient>();
        var available = new Queue<IReadOnlyList<string>>(new IReadOnlyList<string>[]
        {
            new[] { "V-001", "V-002" },
            new[] { "V-002" }
        });
        apiClient
            .Setup(c => c.GetAvailableVehicleIdsAsync(It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => available.Dequeue());

        var coordinator = Build(store, apiClient);

        // Act
        await coordinator.ClaimInitialVehiclesAsync(CancellationToken.None);

        // Assert
        apiClient.Verify(c => c.ClaimVehicleAsync("V-001", rep1, It.IsAny<CancellationToken>()), Times.Once);
        apiClient.Verify(c => c.ClaimVehicleAsync("V-002", rep2, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── BUG-016 AC-2: object-shaped /vehicles/available no longer crashes claim ───
    // Wires a REAL BackendApiClient (over a stub HttpMessageHandler) into the
    // coordinator so the actual deserialization path is exercised end-to-end — a
    // mocked IBackendApiClient would not have caught the response-shape bug.
    [Fact]
    public async Task GivenAvailableVehiclesAsObjects_WhenClaimInitialVehiclesAsyncCalled_ThenItClaimsTheFirstFreeVehicleWithoutThrowing()
    {
        // Arrange
        var rep1 = Rep("rep-1");
        var store = StoreWith(rep1);
        store.Setup(s => s.GetValidTokenAsync(It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("rep1-token");

        const string availableJson = """
        [
          { "vehicleId": "V-003", "registration": "REG-003", "equipment": ["tow"] },
          { "vehicleId": "V-004", "registration": "REG-004", "equipment": [] }
        ]
        """;
        var claimRequests = new List<HttpRequestMessage>();
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/vehicles/available")
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(availableJson) };

                claimRequests.Add(req);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        var httpClient = new HttpClient(handlerMock.Object) { BaseAddress = new Uri("https://backend.local") };
        var apiClient = new BackendApiClient(httpClient, store.Object, NullLogger<BackendApiClient>.Instance);
        var coordinator = new FleetClaimCoordinator(
            store.Object, apiClient, new YieldedRepRegistry(), NullLogger<FleetClaimCoordinator>.Instance);

        // Act
        await coordinator.ClaimInitialVehiclesAsync(CancellationToken.None);

        // Assert — the first free vehicle is claimed and no exception propagated
        var claim = Assert.Single(claimRequests);
        Assert.Equal("/vehicles/V-003/claim", claim.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GivenAnOperatedRepWithNoVehicle_WhenRebalanceRuns_ThenAFreeVehicleIsClaimedForThatRep()
    {
        // Arrange — rep-1 holds V-001; rep-2 holds nothing
        var rep1 = Rep("rep-1");
        var rep2 = Rep("rep-2");
        var store = StoreWith(rep1, rep2);

        var apiClient = new Mock<IBackendApiClient>();
        apiClient
            .Setup(c => c.GetAvailableVehicleIdsAsync(rep2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "V-007" });

        var coordinator = Build(store, apiClient);

        var snapshot = new[]
        {
            Row("V-001", claimingRepId: "rep-1"),
            Row("V-007", claimingRepId: null)
        };

        // Act
        await coordinator.RebalanceAsync(snapshot, CancellationToken.None);

        // Assert
        apiClient.Verify(c => c.ClaimVehicleAsync("V-007", rep2, It.IsAny<CancellationToken>()), Times.Once);
        apiClient.Verify(c => c.ClaimVehicleAsync(It.IsAny<string>(), rep1, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GivenAllOperatedRepsHoldVehicles_WhenRebalanceRuns_ThenNoAdditionalClaimsAreMade()
    {
        // Arrange — both reps already hold a vehicle in the snapshot
        var rep1 = Rep("rep-1");
        var rep2 = Rep("rep-2");
        var store = StoreWith(rep1, rep2);

        var apiClient = new Mock<IBackendApiClient>();
        var coordinator = Build(store, apiClient);

        var snapshot = new[]
        {
            Row("V-001", claimingRepId: "rep-1"),
            Row("V-002", claimingRepId: "rep-2")
        };

        // Act
        await coordinator.RebalanceAsync(snapshot, CancellationToken.None);

        // Assert
        apiClient.Verify(
            c => c.ClaimVehicleAsync(It.IsAny<string>(), It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ─── SIM-009 AC-4: yielded reps and vehicles are excluded from rebalancing ───

    [Fact]
    public async Task GivenAYieldedRep_WhenRebalanceRuns_ThenNoVehicleIsClaimedForThatRep()
    {
        // Arrange — rep-1 was yielded to a human; it currently holds no vehicle
        var rep1 = Rep("rep-1");
        var store = StoreWith(rep1);

        var apiClient = new Mock<IBackendApiClient>();
        apiClient
            .Setup(c => c.GetAvailableVehicleIdsAsync(It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "V-005" });

        var registry = new YieldedRepRegistry();
        registry.ObserveAndRecordIfYielded(
            new FleetStateRow("V-001", "rep-1", RepState.Available, HumanControlled: true, ActiveRequestLocation: null));
        var coordinator = Build(store, apiClient, registry);

        var snapshot = new[] { Row("V-005", claimingRepId: null) };

        // Act
        await coordinator.RebalanceAsync(snapshot, CancellationToken.None);

        // Assert — the yielded rep is never given a claim
        apiClient.Verify(c => c.ClaimVehicleAsync(It.IsAny<string>(), rep1, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GivenAYieldedVehicle_WhenRebalanceRuns_ThenThatVehicleIsNotClaimed()
    {
        // Arrange — rep-2 is operable and holds nothing; the only free vehicle (V-009)
        // was yielded with its rep when a human took it "home for the night"
        var rep2 = Rep("rep-2");
        var store = StoreWith(rep2);

        var apiClient = new Mock<IBackendApiClient>();
        apiClient
            .Setup(c => c.GetAvailableVehicleIdsAsync(rep2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "V-009" });

        var registry = new YieldedRepRegistry();
        registry.ObserveAndRecordIfYielded(
            new FleetStateRow("V-009", "rep-1", RepState.Available, HumanControlled: true, ActiveRequestLocation: null));
        var coordinator = Build(store, apiClient, registry);

        var snapshot = new[] { Row("V-009", claimingRepId: null) };

        // Act
        await coordinator.RebalanceAsync(snapshot, CancellationToken.None);

        // Assert — the yielded vehicle is never re-claimed by anyone
        apiClient.Verify(c => c.ClaimVehicleAsync("V-009", It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
