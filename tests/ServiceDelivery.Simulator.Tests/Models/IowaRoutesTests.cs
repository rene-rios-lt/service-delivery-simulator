using ServiceDelivery.Simulator.Models;
using Xunit;

namespace ServiceDelivery.Simulator.Tests.Models;

public class IowaRoutesTests
{
    [Fact]
    public void GivenIowaRoutes_WhenAllRoutesLoaded_ThenEightRoutesExist()
    {
        // Arrange & Act
        var routes = IowaRoutes.All;

        // Assert
        Assert.Equal(8, routes.Count);
    }

    [Fact]
    public void GivenIowaRoutes_WhenAllRoutesLoaded_ThenAllVehicleIdsAreDistinct()
    {
        // Arrange & Act
        var routes = IowaRoutes.All;

        // Assert
        var distinctIds = routes.Select(r => r.VehicleId).Distinct().ToList();
        Assert.Equal(routes.Count, distinctIds.Count);
    }

    [Fact]
    public void GivenIowaRoutes_WhenAllRoutesLoaded_ThenEachRouteHasAtLeastTwoWaypoints()
    {
        // Arrange & Act
        var routes = IowaRoutes.All;

        // Assert
        foreach (var route in routes)
        {
            Assert.True(route.Waypoints.Count >= 2,
                $"Route {route.VehicleId} has {route.Waypoints.Count} waypoint(s) — expected at least 2");
        }
    }

    [Fact]
    public void GivenIowaRoutes_WhenAllRoutesLoaded_ThenAllWaypointsAreWithinIowaBounds()
    {
        // Arrange
        const double minLat = 40.3;
        const double maxLat = 43.6;
        const double minLng = -96.7;
        const double maxLng = -90.1;

        // Act
        var routes = IowaRoutes.All;

        // Assert
        foreach (var route in routes)
        {
            foreach (var waypoint in route.Waypoints)
            {
                Assert.True(
                    waypoint.Latitude >= minLat && waypoint.Latitude <= maxLat,
                    $"Route {route.VehicleId} has waypoint with Latitude {waypoint.Latitude} outside Iowa bounds [{minLat}, {maxLat}]");

                Assert.True(
                    waypoint.Longitude >= minLng && waypoint.Longitude <= maxLng,
                    $"Route {route.VehicleId} has waypoint with Longitude {waypoint.Longitude} outside Iowa bounds [{minLng}, {maxLng}]");
            }
        }
    }
}
