using Microsoft.Extensions.Logging;
using Moq;
using ServiceDelivery.Simulator.Models;
using ServiceDelivery.Simulator.Services;
using ServiceDelivery.Simulator.Workers;
using Xunit;

namespace ServiceDelivery.Simulator.Tests.Workers;

// Under SIM-008 topology A the VehicleWorker is a plain per-vehicle drive object: the
// FleetReconciler supplies its fleet-state row + resolved drive mode each tick and the
// worker posts the resulting position. The IdleLoop branch preserves the SIM-004
// loop-advance behaviour; Navigate/Hold geometry is owned by SIM-006.
public class VehicleWorkerRouteTests
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

    private static FleetStateRow IdleRow(string vehicleId = "V-TEST") =>
        new(vehicleId, "rep-1", RepState.Available, HumanControlled: false, ActiveRequestLocation: null);

    // ─── AC-2: IdleLoop mode advances one waypoint per drive and posts it ─────

    [Fact]
    public async Task GivenIdleLoopMode_WhenWorkerDrives_ThenPositionAdvancesAlongLoop()
    {
        // Arrange
        var route = BuildTestRoute();
        VehiclePosition? capturedPosition = null;

        var apiClientMock = new Mock<IBackendApiClient>();
        apiClientMock
            .Setup(c => c.PostPositionAsync(It.IsAny<VehiclePosition>(), It.IsAny<CancellationToken>()))
            .Callback<VehiclePosition, CancellationToken>((pos, _) => capturedPosition = pos)
            .Returns(Task.CompletedTask);

        var worker = new VehicleWorker(route, apiClientMock.Object, NullLogger());

        // Act
        await worker.DriveAsync(IdleRow(), VehicleDriveMode.IdleLoop, CancellationToken.None);

        // Assert
        Assert.NotNull(capturedPosition);
        Assert.Equal(route.Waypoints[1].Latitude, capturedPosition!.Latitude);
        Assert.Equal(route.Waypoints[1].Longitude, capturedPosition.Longitude);
        Assert.Equal(route.VehicleId, capturedPosition.VehicleId);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task GivenIdleLoopModeAtWaypointN_WhenWorkerDrives_ThenPositionAdvancesToWaypointNPlusOne(int startIndex)
    {
        // Arrange
        var route = BuildTestRoute(waypointCount: 6);

        var apiClientMock = new Mock<IBackendApiClient>();
        VehiclePosition? capturedPosition = null;
        apiClientMock
            .Setup(c => c.PostPositionAsync(It.IsAny<VehiclePosition>(), It.IsAny<CancellationToken>()))
            .Callback<VehiclePosition, CancellationToken>((pos, _) => capturedPosition = pos)
            .Returns(Task.CompletedTask);

        var worker = new VehicleWorker(route, apiClientMock.Object, NullLogger());

        // Advance to the desired start index
        for (int i = 0; i < startIndex; i++)
        {
            await worker.DriveAsync(IdleRow(), VehicleDriveMode.IdleLoop, CancellationToken.None);
        }

        // Act — one more drive from startIndex
        await worker.DriveAsync(IdleRow(), VehicleDriveMode.IdleLoop, CancellationToken.None);

        // Assert
        var expectedWaypoint = route.Waypoints[startIndex + 1];
        Assert.NotNull(capturedPosition);
        Assert.Equal(expectedWaypoint.Latitude, capturedPosition!.Latitude);
        Assert.Equal(expectedWaypoint.Longitude, capturedPosition.Longitude);
    }

    // ─── AC-2: wrap from last waypoint back to first in IdleLoop mode ─────────

    [Fact]
    public async Task GivenIdleLoopModeAtLastWaypoint_WhenWorkerDrives_ThenPositionWrapsToFirstWaypoint()
    {
        // Arrange
        var route = BuildTestRoute(waypointCount: 3);
        VehiclePosition? capturedPosition = null;

        var apiClientMock = new Mock<IBackendApiClient>();
        apiClientMock
            .Setup(c => c.PostPositionAsync(It.IsAny<VehiclePosition>(), It.IsAny<CancellationToken>()))
            .Callback<VehiclePosition, CancellationToken>((pos, _) => capturedPosition = pos)
            .Returns(Task.CompletedTask);

        var worker = new VehicleWorker(route, apiClientMock.Object, NullLogger());

        // Advance to the last waypoint (index 2 of 3)
        await worker.DriveAsync(IdleRow(), VehicleDriveMode.IdleLoop, CancellationToken.None); // 0 → 1
        await worker.DriveAsync(IdleRow(), VehicleDriveMode.IdleLoop, CancellationToken.None); // 1 → 2 (last)

        // Act — drive from the last waypoint should wrap to index 0
        await worker.DriveAsync(IdleRow(), VehicleDriveMode.IdleLoop, CancellationToken.None);

        // Assert
        var firstWaypoint = route.Waypoints[0];
        Assert.NotNull(capturedPosition);
        Assert.Equal(firstWaypoint.Latitude, capturedPosition!.Latitude);
        Assert.Equal(firstWaypoint.Longitude, capturedPosition.Longitude);
    }

    // ─── AC-2: IdleLoop drive posts to the position endpoint with the right payload ─

    [Fact]
    public async Task GivenIdleLoopMode_WhenWorkerDrives_ThenPostPositionAsyncCalledWithCorrectPayload()
    {
        // Arrange
        var route = BuildTestRoute();
        VehiclePosition? capturedPosition = null;
        var callCount = 0;

        var apiClientMock = new Mock<IBackendApiClient>();
        apiClientMock
            .Setup(c => c.PostPositionAsync(It.IsAny<VehiclePosition>(), It.IsAny<CancellationToken>()))
            .Callback<VehiclePosition, CancellationToken>((pos, _) =>
            {
                capturedPosition = pos;
                callCount++;
            })
            .Returns(Task.CompletedTask);

        var worker = new VehicleWorker(route, apiClientMock.Object, NullLogger());

        // Act
        await worker.DriveAsync(IdleRow(), VehicleDriveMode.IdleLoop, CancellationToken.None);

        // Assert
        Assert.Equal(1, callCount);
        Assert.NotNull(capturedPosition);
        Assert.Equal(route.VehicleId, capturedPosition!.VehicleId);
        Assert.Equal(route.Waypoints[1].Latitude, capturedPosition.Latitude);
        Assert.Equal(route.Waypoints[1].Longitude, capturedPosition.Longitude);
    }

    // ─── A transient network failure on drive is caught, logged, and swallowed ─

    [Fact]
    public async Task GivenPostPositionThrowsNetworkException_WhenWorkerDrives_ThenWorkerCatchesAndContinues()
    {
        // Arrange
        var route = BuildTestRoute();
        var loggerMock = new Mock<ILogger<VehicleWorker>>();

        var apiClientMock = new Mock<IBackendApiClient>();
        apiClientMock
            .Setup(c => c.PostPositionAsync(It.IsAny<VehiclePosition>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Simulated network error"));

        var worker = new VehicleWorker(route, apiClientMock.Object, loggerMock.Object);

        // Act — DriveAsync should not throw despite PostPositionAsync throwing
        var exception = await Record.ExceptionAsync(
            () => worker.DriveAsync(IdleRow(), VehicleDriveMode.IdleLoop, CancellationToken.None));

        // Assert — no exception propagated; error was logged
        Assert.Null(exception);
        loggerMock.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.Is<Exception>(ex => ex is HttpRequestException),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
