using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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

        var coordinator = new FleetClaimCoordinator(store.Object, apiClient.Object, NullLogger<FleetClaimCoordinator>.Instance);

        // Act
        await coordinator.ClaimInitialVehiclesAsync(CancellationToken.None);

        // Assert
        apiClient.Verify(c => c.ClaimVehicleAsync("V-001", rep1, It.IsAny<CancellationToken>()), Times.Once);
        apiClient.Verify(c => c.ClaimVehicleAsync("V-002", rep2, It.IsAny<CancellationToken>()), Times.Once);
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

        var coordinator = new FleetClaimCoordinator(store.Object, apiClient.Object, NullLogger<FleetClaimCoordinator>.Instance);

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
        var coordinator = new FleetClaimCoordinator(store.Object, apiClient.Object, NullLogger<FleetClaimCoordinator>.Instance);

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
}
