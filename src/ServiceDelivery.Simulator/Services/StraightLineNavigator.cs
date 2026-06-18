using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

// Pure straight-line navigation geometry. Distances use the Haversine formula so the
// per-tick step and the arrival threshold are real ground distances. Both tuning
// values are code constants (no config knob, matching the SIM-010 dwell-constant
// precedent). This is the single home of the "reached" predicate so the position
// worker and the arrival reporter agree on one threshold.
public sealed class StraightLineNavigator : IStraightLineNavigator
{
    private const double EarthRadiusMeters = 6_371_000.0;

    // O-2: the truck advances at a realistic highway speed each tick. The per-tick
    // step distance is DERIVED from the navigation speed and the tick interval so the
    // constants document the intent (65 mph) rather than a magic metres-per-tick value.
    // The navigator stays pure: both the speed and the tick interval are code constants
    // (no config knob, matching the SIM-010 dwell-constant precedent). The tick-interval
    // constant mirrors SimulatorOptions.PositionUpdateIntervalSeconds (default 3) which
    // the FleetReconciler runs on; if that default changes this constant must follow.
    private const double NavigationSpeedMph = 65.0;
    private const double MetersPerMile = 1609.344;
    private const double SecondsPerHour = 3600.0;
    private const double TickIntervalSeconds = 3.0;

    // 65 mph ≈ 29.0576 m/s × 3 s ≈ 87.17 m per tick.
    private const double StepDistanceMeters =
        NavigationSpeedMph * MetersPerMile / SecondsPerHour * TickIntervalSeconds;

    // O-3: "reached" tolerance. Within this Haversine distance the truck snaps to the
    // exact target to avoid jitter around the destination.
    private const double ArrivalThresholdMeters = 50.0;

    public NavigationStep Step(double currentLat, double currentLng, double targetLat, double targetLng)
    {
        if (HasReached(currentLat, currentLng, targetLat, targetLng))
            return new NavigationStep(targetLat, targetLng);

        double distance = HaversineMeters(currentLat, currentLng, targetLat, targetLng);
        double fraction = StepDistanceMeters / distance;

        double nextLat = currentLat + (targetLat - currentLat) * fraction;
        double nextLng = currentLng + (targetLng - currentLng) * fraction;

        return HasReached(nextLat, nextLng, targetLat, targetLng)
            ? new NavigationStep(targetLat, targetLng)
            : new NavigationStep(nextLat, nextLng);
    }

    public bool HasReached(double currentLat, double currentLng, double targetLat, double targetLng) =>
        HaversineMeters(currentLat, currentLng, targetLat, targetLng) <= ArrivalThresholdMeters;

    public int NearestWaypointIndex(double currentLat, double currentLng, IReadOnlyList<RouteWaypoint> waypoints)
    {
        int nearestIndex = 0;
        double nearestDistance = double.MaxValue;

        for (int i = 0; i < waypoints.Count; i++)
        {
            double distance = HaversineMeters(currentLat, currentLng, waypoints[i].Latitude, waypoints[i].Longitude);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestIndex = i;
            }
        }

        return nearestIndex;
    }

    private static double HaversineMeters(double lat1, double lng1, double lat2, double lng2)
    {
        double dLat = ToRadians(lat2 - lat1);
        double dLng = ToRadians(lng2 - lng1);
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                   + Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2))
                   * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return EarthRadiusMeters * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
