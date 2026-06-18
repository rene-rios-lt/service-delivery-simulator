using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceDelivery.Simulator.Models;
using ServiceDelivery.Simulator.Services;
using ServiceDelivery.Simulator.Workers;
using Xunit;

namespace ServiceDelivery.Simulator.Tests.Workers;

// SIM-006: the FleetPositionDriver already holds one VehicleWorker per vehicle, so it is
// the natural IVehiclePositionProvider — exposing each truck's current simulator-tracked
// position to the ArrivalReporter without the position driver itself making rep actions.
public class FleetPositionDriverTests
{
    private static VehicleRoute Route(string vehicleId) =>
        new()
        {
            VehicleId = vehicleId,
            Waypoints = new[] { new RouteWaypoint(41.0, -93.0), new RouteWaypoint(41.1, -93.1) }
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

    // ─── AC-2 support: the driver exposes a driven truck's current position ───

    [Fact]
    public async Task GivenAVehicleHasBeenDriven_WhenTryGetPosition_ThenReturnsItsLastPostedPosition()
    {
        // Arrange
        var (api, posted) = ApiCapturingPositions();
        var worker = new VehicleWorker(Route("V-1"), api.Object, new StraightLineNavigator(),
            NullLogger<VehicleWorker>.Instance);
        var driver = new FleetPositionDriver(new[] { worker }, NullLogger<FleetPositionDriver>.Instance);

        var navigateRow = new FleetStateRow("V-1", "rep-1", RepState.EnRoute, false, new RequesterLocation(42.0, -93.0));
        await driver.DriveAsync(navigateRow, VehicleDriveMode.Navigate, CancellationToken.None);

        // Act
        bool found = driver.TryGetPosition("V-1", out var position);

        // Assert
        Assert.True(found);
        var post = Assert.Single(posted);
        Assert.Equal(post.Latitude, position.Lat);
        Assert.Equal(post.Longitude, position.Lng);
    }

    [Fact]
    public void GivenAnUnknownVehicleId_WhenTryGetPosition_ThenReturnsFalse()
    {
        // Arrange
        var (api, _) = ApiCapturingPositions();
        var worker = new VehicleWorker(Route("V-1"), api.Object, new StraightLineNavigator(),
            NullLogger<VehicleWorker>.Instance);
        var driver = new FleetPositionDriver(new[] { worker }, NullLogger<FleetPositionDriver>.Instance);

        // Act
        bool found = driver.TryGetPosition("V-UNKNOWN", out _);

        // Assert
        Assert.False(found);
    }
}
