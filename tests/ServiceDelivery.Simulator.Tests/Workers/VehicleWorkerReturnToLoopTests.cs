using Microsoft.Extensions.Logging;
using Moq;
using ServiceDelivery.Simulator.Models;
using ServiceDelivery.Simulator.Services;
using ServiceDelivery.Simulator.Workers;
using Xunit;

namespace ServiceDelivery.Simulator.Tests.Workers;

// SIM-007: return to loop after job completion. After a Navigate/Hold excursion the
// worker's cached job position (_lastLat/_lastLng) sits at the completed-job location
// while the loop index is stale. On the first Available IdleLoop tick following such an
// excursion the worker reattaches to the Haversine-nearest loop waypoint, clears cached
// job-nav state, and posts that waypoint; the next tick resumes ordinary traversal.
// Steady-state loop ticks (no preceding excursion) advance normally and never reattach.
// An Offline/parked row triggers no loop action at all (parking is SIM-009's job).
public class VehicleWorkerReturnToLoopTests
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

    private static FleetStateRow NavigateRow(double lat, double lng) =>
        new("V-TEST", "rep-1", RepState.EnRoute, false, new RequesterLocation(lat, lng));

    private static FleetStateRow HoldRow(double lat, double lng) =>
        new("V-TEST", "rep-1", RepState.OnSite, false, new RequesterLocation(lat, lng));

    private static FleetStateRow AvailableLoopRow() =>
        new("V-TEST", "rep-1", RepState.Available, false, null);

    private static FleetStateRow OfflineLoopRow() =>
        new("V-TEST", null, RepState.Offline, false, null);

    private static (Mock<IBackendApiClient> api, List<VehiclePosition> posted) ApiCapturingPositions()
    {
        var posted = new List<VehiclePosition>();
        var api = new Mock<IBackendApiClient>();
        api.Setup(c => c.PostPositionAsync(It.IsAny<VehiclePosition>(), It.IsAny<CancellationToken>()))
            .Callback<VehiclePosition, CancellationToken>((pos, _) => posted.Add(pos))
            .Returns(Task.CompletedTask);
        return (api, posted);
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

    private static int NearestWaypoint(VehicleRoute route, double lat, double lng)
    {
        int nearest = 0;
        double best = double.MaxValue;
        for (int i = 0; i < route.Waypoints.Count; i++)
        {
            double d = Haversine(lat, lng, route.Waypoints[i].Latitude, route.Waypoints[i].Longitude);
            if (d < best) { best = d; nearest = i; }
        }
        return nearest;
    }

    // ─── AC-1: reattach to the nearest loop waypoint after a completed job ─────

    [Fact]
    public async Task GivenAWorkerThatJustCompletedAJob_WhenDrivenInIdleLoop_ThenItReattachesToTheNearestLoopWaypoint()
    {
        // Arrange — drive Navigate ticks toward a target near waypoint index 4 so the
        // completed-job position lands far from the stale index-0 starting point
        var route = BuildTestRoute();
        var (api, posted) = ApiCapturingPositions();
        var worker = new VehicleWorker(route, api.Object, new StraightLineNavigator(), NullLogger());
        var navigateTarget = new RequesterLocation(route.Waypoints[4].Latitude, route.Waypoints[4].Longitude);

        // Act — navigate (excursion), then one Available IdleLoop tick
        for (int i = 0; i < 60; i++)
            await worker.DriveAsync(NavigateRow(navigateTarget.Lat, navigateTarget.Lng), VehicleDriveMode.Navigate, CancellationToken.None);
        worker.TryGetCurrentPosition(out var jobPosition);
        await worker.DriveAsync(AvailableLoopRow(), VehicleDriveMode.IdleLoop, CancellationToken.None);

        // Assert — the loop post equals the Haversine-nearest waypoint to the job position
        int expectedIndex = NearestWaypoint(route, jobPosition.Lat, jobPosition.Lng);
        var loopPost = posted[^1];
        Assert.Equal(route.Waypoints[expectedIndex].Latitude, loopPost.Latitude);
        Assert.Equal(route.Waypoints[expectedIndex].Longitude, loopPost.Longitude);
    }

    // ─── AC-2: resume ordinary traversal from the reattached waypoint ─────────

    [Fact]
    public async Task GivenAWorkerReattachedToNearestWaypoint_WhenDrivenAgainInIdleLoop_ThenItAdvancesToTheNextWaypointInOrder()
    {
        // Arrange — navigate (excursion), then reattach on the first IdleLoop tick
        var route = BuildTestRoute();
        var (api, posted) = ApiCapturingPositions();
        var worker = new VehicleWorker(route, api.Object, new StraightLineNavigator(), NullLogger());
        var navigateTarget = new RequesterLocation(route.Waypoints[4].Latitude, route.Waypoints[4].Longitude);

        for (int i = 0; i < 60; i++)
            await worker.DriveAsync(NavigateRow(navigateTarget.Lat, navigateTarget.Lng), VehicleDriveMode.Navigate, CancellationToken.None);
        worker.TryGetCurrentPosition(out var jobPosition);
        int reattachedIndex = NearestWaypoint(route, jobPosition.Lat, jobPosition.Lng);

        // Act — first IdleLoop tick reattaches, second IdleLoop tick resumes traversal
        await worker.DriveAsync(AvailableLoopRow(), VehicleDriveMode.IdleLoop, CancellationToken.None);
        await worker.DriveAsync(AvailableLoopRow(), VehicleDriveMode.IdleLoop, CancellationToken.None);

        // Assert — the second loop post is the ordinary (index+1) % Count successor
        int expectedNext = (reattachedIndex + 1) % route.Waypoints.Count;
        var secondLoopPost = posted[^1];
        Assert.Equal(route.Waypoints[expectedNext].Latitude, secondLoopPost.Latitude);
        Assert.Equal(route.Waypoints[expectedNext].Longitude, secondLoopPost.Longitude);
    }

    // ─── AC-2 regression guard: steady-state loop advances 0→1→2 without reattach

    [Fact]
    public async Task GivenAWorkerAlreadyLooping_WhenDrivenInIdleLoop_ThenItAdvancesNormallyWithoutReattaching()
    {
        // Arrange — a worker that has never left the loop
        var route = BuildTestRoute();
        var (api, posted) = ApiCapturingPositions();
        var worker = new VehicleWorker(route, api.Object, new StraightLineNavigator(), NullLogger());

        // Act — three consecutive IdleLoop ticks (no excursion ever)
        await worker.DriveAsync(AvailableLoopRow(), VehicleDriveMode.IdleLoop, CancellationToken.None);
        await worker.DriveAsync(AvailableLoopRow(), VehicleDriveMode.IdleLoop, CancellationToken.None);
        await worker.DriveAsync(AvailableLoopRow(), VehicleDriveMode.IdleLoop, CancellationToken.None);

        // Assert — posts advance 1, 2, 3 (blind ordinary traversal from index 0); no
        // reattach altered the path
        Assert.Equal(3, posted.Count);
        Assert.Equal(route.Waypoints[1].Latitude, posted[0].Latitude);
        Assert.Equal(route.Waypoints[2].Latitude, posted[1].Latitude);
        Assert.Equal(route.Waypoints[3].Latitude, posted[2].Latitude);
    }

    // ─── AC-3: job-nav state cleared — a new Navigate starts from the loop ────

    [Fact]
    public async Task GivenAWorkerThatCompletedAJob_WhenReturnedToLoopThenAssignedANewJob_ThenNavigateStartsFromTheLoopNotTheOldJobPosition()
    {
        // Arrange — navigate toward a first job target, reattach to the loop, then drive
        // a fresh Navigate toward a different target. The new navigate must step from the
        // reattached loop waypoint, not from the stale old-job position.
        var route = BuildTestRoute();
        var (api, posted) = ApiCapturingPositions();
        var worker = new VehicleWorker(route, api.Object, new StraightLineNavigator(), NullLogger());
        var firstJob = new RequesterLocation(route.Waypoints[5].Latitude, route.Waypoints[5].Longitude);

        for (int i = 0; i < 80; i++)
            await worker.DriveAsync(NavigateRow(firstJob.Lat, firstJob.Lng), VehicleDriveMode.Navigate, CancellationToken.None);
        worker.TryGetCurrentPosition(out var oldJobPosition);

        await worker.DriveAsync(AvailableLoopRow(), VehicleDriveMode.IdleLoop, CancellationToken.None);
        var reattachWaypoint = posted[^1];

        // Act — a fresh Navigate toward a new, different target
        var newJob = new RequesterLocation(40.0, -95.0);
        await worker.DriveAsync(NavigateRow(newJob.Lat, newJob.Lng), VehicleDriveMode.Navigate, CancellationToken.None);
        var firstNewNavigatePost = posted[^1];

        // Assert — the first new-navigate step is one bounded step from the reattached
        // loop waypoint, not from the old job position (which is far away)
        double fromReattach = Haversine(firstNewNavigatePost.Latitude, firstNewNavigatePost.Longitude,
            reattachWaypoint.Latitude, reattachWaypoint.Longitude);
        double fromOldJob = Haversine(firstNewNavigatePost.Latitude, firstNewNavigatePost.Longitude,
            oldJobPosition.Lat, oldJobPosition.Lng);
        Assert.True(fromReattach < fromOldJob,
            "a new navigate must step from the reattached loop waypoint, not the cleared old-job position");
    }

    // ─── AC-4 (negative): an Offline/parked row triggers no loop resume ───────

    [Fact]
    public async Task GivenAnOfflineRepRow_WhenDrivenInIdleLoop_ThenTheWorkerTakesNoLoopResumeAction()
    {
        // Arrange — a Navigate excursion (so an excursion flag would be set), then an
        // Offline IdleLoop tick (human went off-duty); VehicleDriveResolver maps Offline
        // to IdleLoop, so the worker must gate on RepState
        var route = BuildTestRoute();
        var (api, posted) = ApiCapturingPositions();
        var worker = new VehicleWorker(route, api.Object, new StraightLineNavigator(), NullLogger());

        for (int i = 0; i < 60; i++)
            await worker.DriveAsync(NavigateRow(route.Waypoints[4].Latitude, route.Waypoints[4].Longitude),
                VehicleDriveMode.Navigate, CancellationToken.None);
        int postsAfterExcursion = posted.Count;

        // Act — an Offline IdleLoop tick
        await worker.DriveAsync(OfflineLoopRow(), VehicleDriveMode.IdleLoop, CancellationToken.None);

        // Assert — no new position was posted (no reattach, no resume)
        Assert.Equal(postsAfterExcursion, posted.Count);
    }

    [Fact]
    public async Task GivenAnOfflineRepRowAfterAJob_WhenDrivenInIdleLoop_ThenTheWaypointIndexDoesNotAdvanceAndNoMovedPositionIsPosted()
    {
        // Arrange — drive the loop a few ticks so the index sits at a known value, then
        // an Offline IdleLoop tick must not advance it or post a moved position
        var route = BuildTestRoute();
        var (api, posted) = ApiCapturingPositions();
        var worker = new VehicleWorker(route, api.Object, new StraightLineNavigator(), NullLogger());

        await worker.DriveAsync(AvailableLoopRow(), VehicleDriveMode.IdleLoop, CancellationToken.None);
        await worker.DriveAsync(AvailableLoopRow(), VehicleDriveMode.IdleLoop, CancellationToken.None);
        int postsBeforeOffline = posted.Count;

        // Act — an Offline IdleLoop tick
        await worker.DriveAsync(OfflineLoopRow(), VehicleDriveMode.IdleLoop, CancellationToken.None);

        // Assert — no new post; and a subsequent Available tick resumes from where the
        // index was (waypoint[3]), proving the Offline tick advanced nothing
        Assert.Equal(postsBeforeOffline, posted.Count);

        await worker.DriveAsync(AvailableLoopRow(), VehicleDriveMode.IdleLoop, CancellationToken.None);
        var resumePost = posted[^1];
        Assert.Equal(route.Waypoints[3].Latitude, resumePost.Latitude);
        Assert.Equal(route.Waypoints[3].Longitude, resumePost.Longitude);
    }
}
