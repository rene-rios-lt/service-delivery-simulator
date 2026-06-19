using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceDelivery.Simulator.Models;
using ServiceDelivery.Simulator.Services;
using ServiceDelivery.Simulator.Workers;
using Xunit;

namespace ServiceDelivery.Simulator.Tests.Workers;

// BUG-017: a VehicleRoute is pure patrol GEOMETRY whose VehicleId is a cosmetic
// registration label (e.g. "V-1"). The backend keys vehicles by GUID, and every
// FleetStateRow carries that GUID as row.VehicleId — distinct from any route label.
// The driver must resolve workers by the backend GUID (not the registration) and the
// posted VehiclePosition must carry that GUID. These tests therefore use DISTINCT values
// for the route registration and the fleet-state GUID so the identity contract is
// exercised rather than hidden by a coincidental match.
public class FleetPositionDriverTests
{
    private const string Registration = "V-1";
    private const string BackendGuid = "30000000-0000-0000-0000-000000000001";

    private static VehicleRoute Route(string registration = Registration, double latBase = 41.0) =>
        new()
        {
            VehicleId = registration,
            Waypoints = new[] { new RouteWaypoint(latBase, -93.0), new RouteWaypoint(latBase + 0.1, -93.1) }
        };

    private static (Mock<IBackendApiClient> api, List<VehiclePosition> posted) ApiCapturingPositions()
    {
        var posted = new List<VehiclePosition>();
        var api = new Mock<IBackendApiClient>();
        api.Setup(c => c.PostPositionAsync(It.IsAny<VehiclePosition>(), It.IsAny<CancellationToken>()))
            .Callback<VehiclePosition, CancellationToken>((pos, _) => posted.Add(pos))
            .Returns(Task.CompletedTask);
        return (api, posted);
    }

    private static VehicleWorker NewWorker(Mock<IBackendApiClient> api, string registration = Registration, double latBase = 41.0) =>
        new(Route(registration, latBase), api.Object, new StraightLineNavigator(),
            NullLogger<VehicleWorker>.Instance);

    private static FleetStateRow NavigateRow(string guid) =>
        new(guid, "rep-1", RepState.EnRoute, false, new RequesterLocation(42.0, -93.0));

    private static FleetStateRow IdleRow(string guid) =>
        new(guid, "rep-1", RepState.Available, false, null);

    // ─── AC-1: a GUID-keyed row resolves to a pooled worker and a position is posted ───

    [Fact]
    public async Task GivenAGuidKeyedFleetStateRow_WhenDriven_ThenAWorkerIsResolvedAndPositionIsPosted()
    {
        // Arrange
        var (api, posted) = ApiCapturingPositions();
        var driver = new FleetPositionDriver(new[] { NewWorker(api) }, NullLogger<FleetPositionDriver>.Instance);

        // Act
        await driver.DriveAsync(NavigateRow(BackendGuid), VehicleDriveMode.Navigate, CancellationToken.None);

        // Assert
        Assert.Single(posted);
    }

    // ─── AC-2: the posted VehiclePosition carries the backend GUID, not the registration ───

    [Fact]
    public async Task GivenAGuidKeyedFleetStateRow_WhenDriven_ThenPostedPositionCarriesTheGuidNotTheRegistration()
    {
        // Arrange
        var (api, posted) = ApiCapturingPositions();
        var driver = new FleetPositionDriver(new[] { NewWorker(api) }, NullLogger<FleetPositionDriver>.Instance);

        // Act
        await driver.DriveAsync(NavigateRow(BackendGuid), VehicleDriveMode.Navigate, CancellationToken.None);

        // Assert
        var post = Assert.Single(posted);
        Assert.Equal(BackendGuid, post.VehicleId);
        Assert.NotEqual(Registration, post.VehicleId);
    }

    // ─── AC-2: every operated vehicle posts exactly one position per tick ───

    [Fact]
    public async Task GivenGuidKeyedRowsForAllEightVehicles_WhenEachIsDriven_ThenEachPostsExactlyOnePosition()
    {
        // Arrange
        var (api, posted) = ApiCapturingPositions();
        var workers = Enumerable.Range(0, 8).Select(i => NewWorker(api, $"V-{i}")).ToArray();
        var driver = new FleetPositionDriver(workers, NullLogger<FleetPositionDriver>.Instance);
        var guids = Enumerable.Range(1, 8)
            .Select(i => $"30000000-0000-0000-0000-0000000000{i:D2}")
            .ToArray();

        // Act
        foreach (var guid in guids)
            await driver.DriveAsync(NavigateRow(guid), VehicleDriveMode.Navigate, CancellationToken.None);

        // Assert
        Assert.Equal(8, posted.Count);
        Assert.Equal(guids.OrderBy(g => g), posted.Select(p => p.VehicleId).OrderBy(g => g));
    }

    // ─── AC-3: GUID→worker assignment is stable across ticks ───

    [Fact]
    public async Task GivenAGuidKeyedRow_WhenDrivenOnConsecutiveTicks_ThenTheSameWorkerHandlesItEveryTick()
    {
        // Arrange
        var (api, posted) = ApiCapturingPositions();
        var workerA = NewWorker(api, "V-A", latBase: 41.0);
        var workerB = NewWorker(api, "V-B", latBase: 45.0);
        var driver = new FleetPositionDriver(new[] { workerA, workerB }, NullLogger<FleetPositionDriver>.Instance);
        var guidOne = "30000000-0000-0000-0000-000000000001";
        var guidTwo = "30000000-0000-0000-0000-000000000002";

        // Act — interleave two GUIDs across several ticks
        await driver.DriveAsync(IdleRow(guidOne), VehicleDriveMode.IdleLoop, CancellationToken.None);
        await driver.DriveAsync(IdleRow(guidTwo), VehicleDriveMode.IdleLoop, CancellationToken.None);
        await driver.DriveAsync(IdleRow(guidOne), VehicleDriveMode.IdleLoop, CancellationToken.None);
        await driver.DriveAsync(IdleRow(guidTwo), VehicleDriveMode.IdleLoop, CancellationToken.None);

        // Assert — each GUID's posts all came from one stable worker (distinct loop geometry per worker)
        var guidOnePosts = posted.Where(p => p.VehicleId == guidOne).ToList();
        var guidTwoPosts = posted.Where(p => p.VehicleId == guidTwo).ToList();
        Assert.Equal(2, guidOnePosts.Count);
        Assert.Equal(2, guidTwoPosts.Count);
        // Distinct GUIDs were assigned distinct workers (distinct route geometry).
        Assert.NotEqual(
            (guidOnePosts[0].Latitude, guidOnePosts[0].Longitude),
            (guidTwoPosts[0].Latitude, guidTwoPosts[0].Longitude));
    }

    // ─── AC-3: the provider path resolves the same GUID the driver was driven with ───

    [Fact]
    public async Task GivenAVehicleDrivenByGuid_WhenTryGetPositionWithThatGuid_ThenReturnsItsLastPostedPosition()
    {
        // Arrange
        var (api, posted) = ApiCapturingPositions();
        var driver = new FleetPositionDriver(new[] { NewWorker(api) }, NullLogger<FleetPositionDriver>.Instance);
        await driver.DriveAsync(NavigateRow(BackendGuid), VehicleDriveMode.Navigate, CancellationToken.None);

        // Act
        bool found = driver.TryGetPosition(BackendGuid, out var position);

        // Assert
        Assert.True(found);
        var post = Assert.Single(posted);
        Assert.Equal(post.Latitude, position.Lat);
        Assert.Equal(post.Longitude, position.Lng);
    }

    // ─── AC-3: a GUID never driven returns false (provider and driver share one map) ───

    [Fact]
    public void GivenAGuidNeverDriven_WhenTryGetPosition_ThenReturnsFalse()
    {
        // Arrange
        var (api, _) = ApiCapturingPositions();
        var driver = new FleetPositionDriver(new[] { NewWorker(api) }, NullLogger<FleetPositionDriver>.Instance);

        // Act
        bool found = driver.TryGetPosition("30000000-0000-0000-0000-00000000ffff", out _);

        // Assert
        Assert.False(found);
    }

    // ─── AC-3 (policy): more distinct GUIDs than routes wrap round-robin without throwing ───

    [Fact]
    public async Task GivenMoreGuidsThanRoutes_WhenEachIsDriven_ThenAssignmentWrapsRoundRobinAndAllPost()
    {
        // Arrange
        var (api, posted) = ApiCapturingPositions();
        // A pool of 2 workers but 3 distinct GUIDs forces a round-robin wrap.
        var driver = new FleetPositionDriver(
            new[] { NewWorker(api, "V-0"), NewWorker(api, "V-1") },
            NullLogger<FleetPositionDriver>.Instance);
        var guids = new[]
        {
            "30000000-0000-0000-0000-000000000001",
            "30000000-0000-0000-0000-000000000002",
            "30000000-0000-0000-0000-000000000003",
        };

        // Act — no throw despite exceeding the pool size; all three post under their GUID
        var exception = await Record.ExceptionAsync(async () =>
        {
            foreach (var guid in guids)
                await driver.DriveAsync(NavigateRow(guid), VehicleDriveMode.Navigate, CancellationToken.None);
        });

        // Assert
        Assert.Null(exception);
        Assert.Equal(3, posted.Count);
        Assert.Equal(guids.OrderBy(g => g), posted.Select(p => p.VehicleId).OrderBy(g => g));
    }
}
