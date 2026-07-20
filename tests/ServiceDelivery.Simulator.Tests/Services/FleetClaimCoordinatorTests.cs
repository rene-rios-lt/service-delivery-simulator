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
        // The take-over listing returns the SAME list to every rep (claimed-but-idle
        // vehicles are not removed) — the coordinator must still fan reps out.
        apiClient
            .Setup(c => c.GetAvailableVehicleIdsAsync(It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "V-001", "V-002" });
        apiClient
            .Setup(c => c.ClaimVehicleAsync(It.IsAny<string>(), It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClaimOutcome.Claimed);

        var coordinator = Build(store, apiClient);

        // Act
        await coordinator.ClaimInitialVehiclesAsync(CancellationToken.None);

        // Assert
        apiClient.Verify(c => c.ClaimVehicleAsync("V-001", rep1, It.IsAny<CancellationToken>()), Times.Once);
        apiClient.Verify(c => c.ClaimVehicleAsync("V-002", rep2, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── BUG-052 AC-1: N reps sharing the SAME front-of-list candidate each claim a
    // distinct vehicle. GET /vehicles/available is the human take-over listing, so it
    // returns the SAME list (claimed-but-idle vehicles included) to every rep. A naive
    // FirstOrDefault would hand all reps the same front-of-list vehicle: stampede. ───
    [Fact]
    public async Task GivenNRepsWithSameFrontOfListCandidate_WhenClaimInitialVehiclesRuns_ThenEachRepClaimsADistinctVehicle()
    {
        // Arrange — 4 reps all see the identical available list
        var reps = new[] { Rep("rep-1"), Rep("rep-2"), Rep("rep-3"), Rep("rep-4") };
        var store = StoreWith(reps);

        var claims = new List<(string vehicleId, string repId)>();
        var apiClient = new Mock<IBackendApiClient>();
        apiClient
            .Setup(c => c.GetAvailableVehicleIdsAsync(It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "V-1", "V-2", "V-3", "V-4" });
        apiClient
            .Setup(c => c.ClaimVehicleAsync(It.IsAny<string>(), It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()))
            .Callback<string, RepIdentity, CancellationToken>((v, r, _) => claims.Add((v, r.RepId!)))
            .ReturnsAsync(ClaimOutcome.Claimed);

        var coordinator = Build(store, apiClient);

        // Act
        await coordinator.ClaimInitialVehiclesAsync(CancellationToken.None);

        // Assert — 4 claims, 4 distinct vehicles, one per rep (no stampede)
        Assert.Equal(4, claims.Count);
        Assert.Equal(4, claims.Select(c => c.vehicleId).Distinct().Count());
        Assert.Equal(4, claims.Select(c => c.repId).Distinct().Count());
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

    // ─── BUG-052 AC-2: on a 409 the rep advances to the next candidate ─────────────
    [Fact]
    public async Task GivenA409OnFirstCandidate_WhenClaimingFreeVehicle_ThenRepAdvancesToNextCandidate()
    {
        // Arrange — the rep's first candidate is already taken (409 Conflict); the
        // second is free.
        var rep1 = Rep("rep-1");
        var store = StoreWith(rep1);

        var apiClient = new Mock<IBackendApiClient>();
        apiClient
            .Setup(c => c.GetAvailableVehicleIdsAsync(It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "V-1", "V-2" });
        apiClient
            .Setup(c => c.ClaimVehicleAsync("V-1", rep1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClaimOutcome.Conflict);
        apiClient
            .Setup(c => c.ClaimVehicleAsync("V-2", rep1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClaimOutcome.Claimed);

        var coordinator = Build(store, apiClient);

        // Act
        await coordinator.ClaimInitialVehiclesAsync(CancellationToken.None);

        // Assert — the rep advances past the conflicted V-1 and claims V-2
        apiClient.Verify(c => c.ClaimVehicleAsync("V-1", rep1, It.IsAny<CancellationToken>()), Times.Once);
        apiClient.Verify(c => c.ClaimVehicleAsync("V-2", rep1, It.IsAny<CancellationToken>()), Times.Once);
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
        apiClient
            .Setup(c => c.ClaimVehicleAsync(It.IsAny<string>(), It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClaimOutcome.Claimed);

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

    // ─── BUG-052 AC-4: a vehicle already claimed by an earlier rep in the same pass is
    // skipped by later reps, so no vehicle is claimed twice — even when an earlier rep
    // had to advance past a 409 onto a vehicle a later rep's offset also targets. ────
    [Fact]
    public async Task GivenAVehicleAlreadyClaimedLocally_WhenClaimingForAnotherRep_ThenAlreadyClaimedVehicleIsSkipped()
    {
        // Arrange — 3 reps see the same list; V-2 is externally held (409). Without a
        // local claimed-set, rep-2 advances onto V-3 and rep-3's offset also lands on
        // V-3, double-claiming it.
        var rep1 = Rep("rep-1");
        var rep2 = Rep("rep-2");
        var rep3 = Rep("rep-3");
        var store = StoreWith(rep1, rep2, rep3);

        var apiClient = new Mock<IBackendApiClient>();
        apiClient
            .Setup(c => c.GetAvailableVehicleIdsAsync(It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "V-1", "V-2", "V-3" });
        apiClient
            .Setup(c => c.ClaimVehicleAsync("V-2", It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClaimOutcome.Conflict);
        apiClient
            .Setup(c => c.ClaimVehicleAsync(It.Is<string>(id => id != "V-2"), It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClaimOutcome.Claimed);

        var coordinator = Build(store, apiClient);

        // Act
        await coordinator.ClaimInitialVehiclesAsync(CancellationToken.None);

        // Assert — no vehicle is claimed by more than one rep
        apiClient.Verify(c => c.ClaimVehicleAsync("V-1", It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()), Times.Once);
        apiClient.Verify(c => c.ClaimVehicleAsync("V-3", It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()), Times.Once);
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

    // ─── BUG-052 (binding constraint): a 409 Conflict marks the vehicle locally-claimed
    // (genuinely taken) so a later rep never re-attempts it, whereas a transient
    // failure must NOT — see the two tests below. ─────────────────────────────────
    [Fact]
    public async Task GivenA409ConflictedVehicle_WhenClaimingForAnotherRep_ThenTheConflictedVehicleIsExcluded()
    {
        // Arrange — 2 reps see the same list; V-1 is genuinely taken (409). rep-1 hits
        // V-1's 409 then claims V-2; rep-2 must NOT re-attempt the conflicted V-1.
        var rep1 = Rep("rep-1");
        var rep2 = Rep("rep-2");
        var store = StoreWith(rep1, rep2);

        var apiClient = new Mock<IBackendApiClient>();
        apiClient
            .Setup(c => c.GetAvailableVehicleIdsAsync(It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "V-1", "V-2" });
        apiClient
            .Setup(c => c.ClaimVehicleAsync("V-1", It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClaimOutcome.Conflict);
        apiClient
            .Setup(c => c.ClaimVehicleAsync("V-2", It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClaimOutcome.Claimed);

        var coordinator = Build(store, apiClient);

        // Act
        await coordinator.ClaimInitialVehiclesAsync(CancellationToken.None);

        // Assert — the conflicted V-1 was recorded locally, so rep-2 never re-attempts it
        apiClient.Verify(c => c.ClaimVehicleAsync("V-1", rep2, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GivenATransientClaimFailure_WhenClaimingForAnotherRep_ThenTheFailedVehicleRemainsEligible()
    {
        // Arrange — 2 reps see the same list. rep-1's first candidate V-1 hits a
        // TRANSIENT failure (e.g. 500/network blip) — the vehicle is still genuinely
        // free. rep-1 advances to V-2. V-1 must NOT be marked locally-claimed, so
        // rep-2 can still claim it (contrast with the 409 case above).
        var rep1 = Rep("rep-1");
        var rep2 = Rep("rep-2");
        var store = StoreWith(rep1, rep2);

        var apiClient = new Mock<IBackendApiClient>();
        apiClient
            .Setup(c => c.GetAvailableVehicleIdsAsync(It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "V-1", "V-2" });
        apiClient
            .Setup(c => c.ClaimVehicleAsync("V-1", rep1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClaimOutcome.Failed);
        apiClient
            .Setup(c => c.ClaimVehicleAsync("V-2", rep1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClaimOutcome.Claimed);
        apiClient
            .Setup(c => c.ClaimVehicleAsync("V-1", rep2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClaimOutcome.Claimed);

        var coordinator = Build(store, apiClient);

        // Act
        await coordinator.ClaimInitialVehiclesAsync(CancellationToken.None);

        // Assert — the transiently-failed V-1 stayed eligible and rep-2 claimed it
        apiClient.Verify(c => c.ClaimVehicleAsync("V-1", rep2, It.IsAny<CancellationToken>()), Times.Once);
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
