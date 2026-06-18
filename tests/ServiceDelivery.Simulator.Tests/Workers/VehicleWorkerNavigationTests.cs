using Microsoft.Extensions.Logging;
using Moq;
using ServiceDelivery.Simulator.Models;
using ServiceDelivery.Simulator.Services;
using ServiceDelivery.Simulator.Workers;
using Xunit;

namespace ServiceDelivery.Simulator.Tests.Workers;

// SIM-006: the Navigate and Hold branches of VehicleWorker. Navigate asks the injected
// IStraightLineNavigator for the next bounded step from the worker's last-posted
// position toward row.ActiveRequestLocation and posts it. Hold posts the truck at the
// requester location and advances no loop state. Neither branch ever makes a rep action
// call — the worker drives every truck, including human-controlled ones.
public class VehicleWorkerNavigationTests
{
    private static VehicleRoute BuildTestRoute(int waypointCount = 6) =>
        new()
        {
            VehicleId = "V-TEST",
            Waypoints = Enumerable.Range(0, waypointCount)
                .Select(i => new RouteWaypoint(41.0 + i * 0.1, -93.0 + i * 0.1))
                .ToList()
        };

    private static ILogger<VehicleWorker> NullLogger() =>
        new Mock<ILogger<VehicleWorker>>().Object;

    private static FleetStateRow NavigateRow(double lat, double lng, bool humanControlled = false) =>
        new("V-TEST", "rep-1", RepState.EnRoute, humanControlled, new RequesterLocation(lat, lng));

    private static FleetStateRow HoldRow(double lat, double lng, bool humanControlled = false) =>
        new("V-TEST", "rep-1", RepState.OnSite, humanControlled, new RequesterLocation(lat, lng));

    private static (Mock<IBackendApiClient> api, List<VehiclePosition> posted) ApiCapturingPositions()
    {
        var posted = new List<VehiclePosition>();
        var api = new Mock<IBackendApiClient>();
        api.Setup(c => c.PostPositionAsync(It.IsAny<VehiclePosition>(), It.IsAny<CancellationToken>()))
            .Callback<VehiclePosition, CancellationToken>((pos, _) => posted.Add(pos))
            .Returns(Task.CompletedTask);
        return (api, posted);
    }

    // ─── AC-1: Navigate posts a position that moves toward the requester ──────

    [Fact]
    public async Task GivenNavigateModeWithActiveRequestLocation_WhenWorkerDrives_ThenPostedPositionMovesTowardRequester()
    {
        // Arrange — a real navigator so we exercise true geometry; target far north
        var route = BuildTestRoute();
        var (api, posted) = ApiCapturingPositions();
        var navigator = new StraightLineNavigator();
        var worker = new VehicleWorker(route, api.Object, navigator, NullLogger());

        double targetLat = 42.0, targetLng = -93.0;
        var startWaypoint = route.Waypoints[0];

        // Act
        await worker.DriveAsync(NavigateRow(targetLat, targetLng), VehicleDriveMode.Navigate, CancellationToken.None);

        // Assert — the posted position is closer to the target than the starting waypoint
        var post = Assert.Single(posted);
        double startDistance = Haversine(startWaypoint.Latitude, startWaypoint.Longitude, targetLat, targetLng);
        double postedDistance = Haversine(post.Latitude, post.Longitude, targetLat, targetLng);
        Assert.True(postedDistance < startDistance, "posted position should be closer to the requester");
        Assert.Equal(route.VehicleId, post.VehicleId);
    }

    // ─── AC-2 support: the worker exposes its last-posted position ────────────

    [Fact]
    public async Task GivenANavigateStepPosted_WhenCurrentPositionRead_ThenItMatchesTheLastPostedPosition()
    {
        // Arrange
        var route = BuildTestRoute();
        var (api, posted) = ApiCapturingPositions();
        var worker = new VehicleWorker(route, api.Object, new StraightLineNavigator(), NullLogger());

        // Act
        await worker.DriveAsync(NavigateRow(42.0, -93.0), VehicleDriveMode.Navigate, CancellationToken.None);
        bool found = worker.TryGetCurrentPosition(out var current);

        // Assert
        Assert.True(found);
        var post = Assert.Single(posted);
        Assert.Equal(post.Latitude, current.Lat);
        Assert.Equal(post.Longitude, current.Lng);
    }

    // ─── AC-1: position progressively approaches the requester over many ticks ─

    [Fact]
    public async Task GivenNavigateModeOverMultipleTicks_WhenWorkerDrives_ThenPositionProgressivelyApproachesRequester()
    {
        // Arrange — a far target so several bounded steps are required
        var route = BuildTestRoute();
        var (api, posted) = ApiCapturingPositions();
        var worker = new VehicleWorker(route, api.Object, new StraightLineNavigator(), NullLogger());

        double targetLat = 42.0, targetLng = -93.0;
        var row = NavigateRow(targetLat, targetLng);

        // Act — drive several ticks
        for (int i = 0; i < 5; i++)
            await worker.DriveAsync(row, VehicleDriveMode.Navigate, CancellationToken.None);

        // Assert — each posted position is strictly closer to the target than the previous
        Assert.Equal(5, posted.Count);
        for (int i = 1; i < posted.Count; i++)
        {
            double prev = Haversine(posted[i - 1].Latitude, posted[i - 1].Longitude, targetLat, targetLng);
            double curr = Haversine(posted[i].Latitude, posted[i].Longitude, targetLat, targetLng);
            Assert.True(curr < prev, $"tick {i} should be closer than tick {i - 1}");
        }
    }

    // ─── AC-3: Hold posts at the requester and takes no rep action ────────────

    [Fact]
    public async Task GivenHoldMode_WhenWorkerDrives_ThenPositionHoldsAndNoRepActionTaken()
    {
        // Arrange
        var route = BuildTestRoute();
        var (api, posted) = ApiCapturingPositions();
        var worker = new VehicleWorker(route, api.Object, new StraightLineNavigator(), NullLogger());
        var row = HoldRow(41.5, -93.6);

        // Act
        await worker.DriveAsync(row, VehicleDriveMode.Hold, CancellationToken.None);

        // Assert — held at the requester location; no accept/decline/arrive called
        var post = Assert.Single(posted);
        Assert.Equal(41.5, post.Latitude);
        Assert.Equal(-93.6, post.Longitude);
        api.Verify(c => c.AcceptJobOfferAsync(It.IsAny<string>(), It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
        api.Verify(c => c.DeclineJobOfferAsync(It.IsAny<string>(), It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
        api.Verify(c => c.ArriveAsync(It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── AC-3: a human-controlled truck still navigates, but no rep action ────

    [Fact]
    public async Task GivenAHumanControlledRepInNavigateMode_WhenWorkerDrives_ThenPositionMovesTowardRequesterButNoArrive()
    {
        // Arrange — human-controlled row in Navigate mode
        var route = BuildTestRoute();
        var (api, posted) = ApiCapturingPositions();
        var worker = new VehicleWorker(route, api.Object, new StraightLineNavigator(), NullLogger());

        double targetLat = 42.0, targetLng = -93.0;
        var row = NavigateRow(targetLat, targetLng, humanControlled: true);
        var startWaypoint = route.Waypoints[0];

        // Act
        await worker.DriveAsync(row, VehicleDriveMode.Navigate, CancellationToken.None);

        // Assert — position advanced toward the requester; no rep action ever made
        var post = Assert.Single(posted);
        double startDistance = Haversine(startWaypoint.Latitude, startWaypoint.Longitude, targetLat, targetLng);
        double postedDistance = Haversine(post.Latitude, post.Longitude, targetLat, targetLng);
        Assert.True(postedDistance < startDistance, "human truck should still navigate toward the requester");
        api.Verify(c => c.ArriveAsync(It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── AC-4: loop traversal is suspended while navigating ───────────────────

    [Fact]
    public async Task GivenNavigateMode_WhenWorkerDrives_ThenLoopWaypointIndexDoesNotAdvance()
    {
        // Arrange
        var route = BuildTestRoute();
        var (api, posted) = ApiCapturingPositions();
        var worker = new VehicleWorker(route, api.Object, new StraightLineNavigator(), NullLogger());
        var row = NavigateRow(42.0, -93.0);

        // Act — several Navigate ticks, then a single IdleLoop tick
        for (int i = 0; i < 3; i++)
            await worker.DriveAsync(row, VehicleDriveMode.Navigate, CancellationToken.None);
        await worker.DriveAsync(NavigateRow(0, 0) with { RepState = RepState.Available }, VehicleDriveMode.IdleLoop, CancellationToken.None);

        // Assert — the IdleLoop post is waypoint[1], proving Navigate left the index at 0
        var loopPost = posted[^1];
        Assert.Equal(route.Waypoints[1].Latitude, loopPost.Latitude);
        Assert.Equal(route.Waypoints[1].Longitude, loopPost.Longitude);
    }

    // ─── AC-4: loop traversal is suspended while holding ──────────────────────

    [Fact]
    public async Task GivenHoldMode_WhenWorkerDrives_ThenLoopWaypointIndexDoesNotAdvance()
    {
        // Arrange
        var route = BuildTestRoute();
        var (api, posted) = ApiCapturingPositions();
        var worker = new VehicleWorker(route, api.Object, new StraightLineNavigator(), NullLogger());
        var row = HoldRow(41.5, -93.6);

        // Act — several Hold ticks, then a single IdleLoop tick
        for (int i = 0; i < 3; i++)
            await worker.DriveAsync(row, VehicleDriveMode.Hold, CancellationToken.None);
        await worker.DriveAsync(row with { RepState = RepState.Available }, VehicleDriveMode.IdleLoop, CancellationToken.None);

        // Assert — the IdleLoop post is waypoint[1], proving Hold left the index at 0
        var loopPost = posted[^1];
        Assert.Equal(route.Waypoints[1].Latitude, loopPost.Latitude);
        Assert.Equal(route.Waypoints[1].Longitude, loopPost.Longitude);
    }

    // ─── AC-5: a redirect (changed ActiveRequestLocation) re-navigates ────────

    [Fact]
    public async Task GivenNavigateModeAndChangedActiveRequestLocationBetweenTicks_WhenWorkerDrives_ThenNextStepHeadsToNewTarget()
    {
        // Arrange — first navigate toward target A, then redirect to target B
        var route = BuildTestRoute();
        var (api, posted) = ApiCapturingPositions();
        var worker = new VehicleWorker(route, api.Object, new StraightLineNavigator(), NullLogger());

        var targetA = new RequesterLocation(42.0, -93.0);
        var targetB = new RequesterLocation(40.0, -95.0);

        // Act — one tick toward A, then a tick toward the redirected target B
        await worker.DriveAsync(new FleetStateRow("V-TEST", "rep-1", RepState.EnRoute, false, targetA),
            VehicleDriveMode.Navigate, CancellationToken.None);
        var afterA = posted[^1];

        await worker.DriveAsync(new FleetStateRow("V-TEST", "rep-1", RepState.EnRoute, false, targetB),
            VehicleDriveMode.Navigate, CancellationToken.None);
        var afterB = posted[^1];

        // Assert — the second step moved closer to B than the post-A position was to B
        double beforeRedirect = Haversine(afterA.Latitude, afterA.Longitude, targetB.Lat, targetB.Lng);
        double afterRedirect = Haversine(afterB.Latitude, afterB.Longitude, targetB.Lat, targetB.Lng);
        Assert.True(afterRedirect < beforeRedirect, "next step should head toward the new redirected target");
    }

    [Fact]
    public async Task GivenAHumanControlledRepRedirected_WhenWorkerDrives_ThenNextStepHeadsToNewTarget()
    {
        // Arrange — same redirect, but the rep is human-controlled (still position-driven)
        var route = BuildTestRoute();
        var (api, posted) = ApiCapturingPositions();
        var worker = new VehicleWorker(route, api.Object, new StraightLineNavigator(), NullLogger());

        var targetA = new RequesterLocation(42.0, -93.0);
        var targetB = new RequesterLocation(40.0, -95.0);

        // Act
        await worker.DriveAsync(new FleetStateRow("V-TEST", "rep-1", RepState.EnRoute, true, targetA),
            VehicleDriveMode.Navigate, CancellationToken.None);
        var afterA = posted[^1];

        await worker.DriveAsync(new FleetStateRow("V-TEST", "rep-1", RepState.EnRoute, true, targetB),
            VehicleDriveMode.Navigate, CancellationToken.None);
        var afterB = posted[^1];

        // Assert
        double beforeRedirect = Haversine(afterA.Latitude, afterA.Longitude, targetB.Lat, targetB.Lng);
        double afterRedirect = Haversine(afterB.Latitude, afterB.Longitude, targetB.Lat, targetB.Lng);
        Assert.True(afterRedirect < beforeRedirect, "human truck should also re-navigate to the redirected target");
    }

    private static double Haversine(double lat1, double lng1, double lat2, double lng2)
    {
        const double r = 6_371_000.0;
        double dLat = (lat2 - lat1) * Math.PI / 180.0;
        double dLng = (lng2 - lng1) * Math.PI / 180.0;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                   + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0)
                   * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return r * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}
