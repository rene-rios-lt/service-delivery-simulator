using Microsoft.Extensions.Logging;
using Moq;
using ServiceDelivery.Simulator.Models;
using ServiceDelivery.Simulator.Services;
using ServiceDelivery.Simulator.Workers;
using Xunit;

namespace ServiceDelivery.Simulator.Tests.Workers;

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

    // ─── AC-2: Position advances one waypoint per tick ───────────────────────

    [Fact]
    public async Task GivenAVehicleWorkerAtWaypointZero_WhenTickExecutes_ThenPositionAdvancesToWaypointOne()
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
        await worker.TickAsync(CancellationToken.None);

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
    public async Task GivenAVehicleWorkerAtWaypointN_WhenTickExecutes_ThenPositionAdvancesToWaypointNPlusOne(int startIndex)
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
            await worker.TickAsync(CancellationToken.None);
        }

        // Act — one more tick from startIndex
        await worker.TickAsync(CancellationToken.None);

        // Assert
        var expectedWaypoint = route.Waypoints[startIndex + 1];
        Assert.NotNull(capturedPosition);
        Assert.Equal(expectedWaypoint.Latitude, capturedPosition!.Latitude);
        Assert.Equal(expectedWaypoint.Longitude, capturedPosition.Longitude);
    }

    // ─── AC-3: Wrap from last waypoint back to first ──────────────────────────

    [Fact]
    public async Task GivenAVehicleWorkerAtLastWaypoint_WhenTickExecutes_ThenPositionWrapsToFirstWaypoint()
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
        await worker.TickAsync(CancellationToken.None); // index 0 → 1
        await worker.TickAsync(CancellationToken.None); // index 1 → 2 (last)

        // Act — tick from the last waypoint should wrap to index 0
        await worker.TickAsync(CancellationToken.None);

        // Assert
        var firstWaypoint = route.Waypoints[0];
        Assert.NotNull(capturedPosition);
        Assert.Equal(firstWaypoint.Latitude, capturedPosition!.Latitude);
        Assert.Equal(firstWaypoint.Longitude, capturedPosition.Longitude);
    }

    // ─── AC-1 (SIM-004): POST /vehicles/{id}/position called with correct payload ─

    [Fact]
    public async Task GivenAVehicleAtCurrentPosition_WhenTickExecutes_ThenPostPositionAsyncCalledWithCorrectPayload()
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
        await worker.TickAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, callCount);
        Assert.NotNull(capturedPosition);
        Assert.Equal(route.VehicleId, capturedPosition!.VehicleId);
        Assert.Equal(route.Waypoints[1].Latitude, capturedPosition.Latitude);
        Assert.Equal(route.Waypoints[1].Longitude, capturedPosition.Longitude);
    }

    // ─── AC-4 (SIM-004): Transient network failure is caught, logged, worker continues ─

    [Fact]
    public async Task GivenPostPositionThrowsNetworkException_WhenTickExecutes_ThenWorkerCatchesAndContinues()
    {
        // Arrange
        var route = BuildTestRoute();
        var loggerMock = new Mock<ILogger<VehicleWorker>>();

        var apiClientMock = new Mock<IBackendApiClient>();
        apiClientMock
            .Setup(c => c.PostPositionAsync(It.IsAny<VehiclePosition>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Simulated network error"));

        var worker = new VehicleWorker(route, apiClientMock.Object, loggerMock.Object);

        // Act — TickAsync should not throw despite PostPositionAsync throwing
        var exception = await Record.ExceptionAsync(
            () => worker.TickAsync(CancellationToken.None));

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

    // ─── AC-5: Cancellation token respected ──────────────────────────────────

    [Fact]
    public async Task GivenARunningVehicleWorker_WhenCancellationRequested_ThenWorkerExitsCleanly()
    {
        // Arrange
        var route = BuildTestRoute();
        var cts = new CancellationTokenSource();

        var apiClientMock = new Mock<IBackendApiClient>();
        apiClientMock
            .Setup(c => c.PostPositionAsync(It.IsAny<VehiclePosition>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var worker = new VehicleWorker(route, apiClientMock.Object, NullLogger());

        // Act — start the worker, then cancel
        var workerTask = worker.StartAsync(cts.Token);
        await cts.CancelAsync();

        // Assert — the task completes within 5 seconds and does not throw
        var exception = await Record.ExceptionAsync(
            () => workerTask.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Null(exception);
    }
}
