using ServiceDelivery.Simulator.Services;
using Xunit;

namespace ServiceDelivery.Simulator.Tests.Services;

// Unit tests for the pure straight-line navigation geometry: bounded per-tick step
// toward the target, Haversine arrival threshold, and snap-to-target on arrival.
public class StraightLineNavigatorTests
{
    private static double HaversineMeters(double lat1, double lng1, double lat2, double lng2)
    {
        const double earthRadiusMeters = 6_371_000.0;
        double dLat = (lat2 - lat1) * Math.PI / 180.0;
        double dLng = (lng2 - lng1) * Math.PI / 180.0;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                   + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0)
                   * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return earthRadiusMeters * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    // ─── AC-1: a step moves a bounded distance toward the target ──────────────

    [Fact]
    public void GivenACurrentAndTargetPosition_WhenStep_ThenResultMovesBoundedDistanceTowardTarget()
    {
        // Arrange — a target far enough away that one step cannot reach it
        var navigator = new StraightLineNavigator();
        double currentLat = 41.0, currentLng = -93.0;
        double targetLat = 42.0, targetLng = -93.0;

        double distanceBefore = HaversineMeters(currentLat, currentLng, targetLat, targetLng);

        // Act
        var step = navigator.Step(currentLat, currentLng, targetLat, targetLng);

        // Assert — the step closed distance toward the target but did not reach it
        double distanceAfter = HaversineMeters(step.Lat, step.Lng, targetLat, targetLng);
        Assert.True(distanceAfter < distanceBefore, "step should move closer to the target");
        Assert.False(navigator.HasReached(step.Lat, step.Lng, targetLat, targetLng),
            "a single step should not reach a far target");
    }

    // ─── AC-1: the per-tick step is bounded to a realistic 65 mph over the tick ──

    [Fact]
    public void GivenAFarTarget_WhenStep_ThenDistanceAdvancedMatchesSixtyFiveMphOverTheTick()
    {
        // Arrange — target ~111 km north (far beyond a single bounded step). The
        // expected per-tick step is derived from the documented 65 mph navigation
        // speed over the 3-second tick, so this test records the speed intent rather
        // than a magic metres-per-tick number.
        const double navigationSpeedMph = 65.0;
        const double metersPerMile = 1609.344;
        const double secondsPerHour = 3600.0;
        const int tickIntervalSeconds = 3;
        double speedMetersPerSecond = navigationSpeedMph * metersPerMile / secondsPerHour;
        double expectedStepMeters = speedMetersPerSecond * tickIntervalSeconds; // ≈ 87.17 m

        var navigator = new StraightLineNavigator();
        double currentLat = 41.0, currentLng = -93.0;
        double targetLat = 42.0, targetLng = -93.0;

        // Act
        var step = navigator.Step(currentLat, currentLng, targetLat, targetLng);

        // Assert — advanced distance is the 65 mph step, not the whole gap (±1 m tolerance)
        double advanced = HaversineMeters(currentLat, currentLng, step.Lat, step.Lng);
        Assert.InRange(advanced, expectedStepMeters - 1.0, expectedStepMeters + 1.0);
    }

    // ─── AC-1 / arrival: within threshold the step snaps to the exact target ──

    [Fact]
    public void GivenATargetWithinArrivalThreshold_WhenStep_ThenResultSnapsToTargetAndReportsReached()
    {
        // Arrange — current position ~10 m from the target (inside the 50 m threshold)
        var navigator = new StraightLineNavigator();
        double targetLat = 41.5, targetLng = -93.6;
        double currentLat = 41.5 + 0.0001, currentLng = -93.6;

        // Act
        var step = navigator.Step(currentLat, currentLng, targetLat, targetLng);

        // Assert — snaps to the exact target once within the arrival threshold
        Assert.True(navigator.HasReached(step.Lat, step.Lng, targetLat, targetLng));
        Assert.Equal(targetLat, step.Lat);
        Assert.Equal(targetLng, step.Lng);
    }

    // ─── shared reached predicate: true within threshold, false beyond ────────

    [Fact]
    public void GivenPositionsWithinAndBeyondThreshold_WhenHasReached_ThenReflectsTheArrivalThreshold()
    {
        // Arrange
        var navigator = new StraightLineNavigator();
        double targetLat = 41.5, targetLng = -93.6;
        double nearLat = 41.5 + 0.0001, nearLng = -93.6;
        double farLat = 41.51, farLng = -93.6;

        // Act
        bool near = navigator.HasReached(nearLat, nearLng, targetLat, targetLng);
        bool far = navigator.HasReached(farLat, farLng, targetLat, targetLng);

        // Assert
        Assert.True(near);
        Assert.False(far);
    }
}
