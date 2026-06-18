using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ServiceDelivery.Simulator.Configuration;
using ServiceDelivery.Simulator.Models;
using ServiceDelivery.Simulator.Services;
using ServiceDelivery.Simulator.Workers;
using Xunit;

namespace ServiceDelivery.Simulator.Tests.Workers;

// AC-1: exactly one fleet-state read per tick. AC-2: every vehicle is driven from
// its own fleet-state row. AC-3: auto-decision is gated by the human-controlled flag.
public class FleetReconcilerTests
{
    private static FleetStateRow Row(
        string vehicleId, string repId, RepState state = RepState.Available,
        bool humanControlled = false, RequesterLocation? location = null) =>
        new(vehicleId, repId, state, humanControlled, location);

    private static FleetReconciler BuildReconciler(
        IReadOnlyList<FleetStateRow> snapshot,
        out Mock<IBackendApiClient> apiClient,
        out Mock<IFleetClaimCoordinator> coordinator,
        out Mock<IVehiclePositionDriver> driver,
        out Mock<IAutoDecisionEngine> autoDecision,
        out Mock<IFleetStateView> fleetStateView)
    {
        apiClient = new Mock<IBackendApiClient>();
        apiClient.Setup(c => c.GetFleetStateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);

        coordinator = new Mock<IFleetClaimCoordinator>();
        driver = new Mock<IVehiclePositionDriver>();
        autoDecision = new Mock<IAutoDecisionEngine>();
        fleetStateView = new Mock<IFleetStateView>();

        var resolver = new VehicleDriveResolver();
        var gate = new RepOperationGate();
        var options = Options.Create(new SimulatorOptions { PositionUpdateIntervalSeconds = 3 });

        return new FleetReconciler(
            apiClient.Object, coordinator.Object, resolver, gate,
            driver.Object, autoDecision.Object, fleetStateView.Object, options,
            NullLogger<FleetReconciler>.Instance);
    }

    [Fact]
    public async Task GivenAReconciler_WhenTickRuns_ThenFleetStateIsReadExactlyOnce()
    {
        // Arrange
        var snapshot = new[] { Row("V-001", "rep-1"), Row("V-002", "rep-2") };
        var reconciler = BuildReconciler(snapshot, out var apiClient, out _, out _, out _, out _);

        // Act
        await reconciler.TickAsync(CancellationToken.None);

        // Assert
        apiClient.Verify(c => c.GetFleetStateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GivenAMultiVehicleFleetState_WhenTickRuns_ThenEveryVehicleIsDrivenFromItsRow()
    {
        // Arrange
        var rowA = Row("V-001", "rep-1", RepState.Available);
        var rowB = Row("V-002", "rep-2", RepState.OnSite, location: new RequesterLocation(41.6, -93.7));
        var reconciler = BuildReconciler(new[] { rowA, rowB }, out _, out _, out var driver, out _, out _);

        // Act
        await reconciler.TickAsync(CancellationToken.None);

        // Assert
        driver.Verify(d => d.DriveAsync(rowA, VehicleDriveMode.IdleLoop, It.IsAny<CancellationToken>()), Times.Once);
        driver.Verify(d => d.DriveAsync(rowB, VehicleDriveMode.Hold, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GivenAHumanControlledRepInFleetState_WhenTickRuns_ThenAutoDecisionIsNotInvokedForThatRep()
    {
        // Arrange
        var humanRow = Row("V-001", "rep-1", humanControlled: true);
        var reconciler = BuildReconciler(new[] { humanRow }, out _, out _, out _, out var autoDecision, out _);

        // Act
        await reconciler.TickAsync(CancellationToken.None);

        // Assert
        autoDecision.Verify(a => a.RunAsync(humanRow, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GivenANonHumanControlledRepInFleetState_WhenTickRuns_ThenAutoDecisionIsInvokedForThatRep()
    {
        // Arrange
        var row = Row("V-001", "rep-1", humanControlled: false);
        var reconciler = BuildReconciler(new[] { row }, out _, out _, out _, out var autoDecision, out _);

        // Act
        await reconciler.TickAsync(CancellationToken.None);

        // Assert
        autoDecision.Verify(a => a.RunAsync(row, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GivenAReconciler_WhenTickRuns_ThenRebalanceIsInvokedWithTheSnapshot()
    {
        // Arrange
        var snapshot = new[] { Row("V-001", "rep-1") };
        var reconciler = BuildReconciler(snapshot, out _, out var coordinator, out _, out _, out _);

        // Act
        await reconciler.TickAsync(CancellationToken.None);

        // Assert
        coordinator.Verify(c => c.RebalanceAsync(snapshot, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── SIM-005 AC-1 plumbing: each tick publishes the snapshot for the offer engine ─

    [Fact]
    public async Task GivenAReconciler_WhenTickRuns_ThenTheReadSnapshotIsPublishedIntoTheFleetStateView()
    {
        // Arrange
        var snapshot = new[] { Row("V-001", "rep-1"), Row("V-002", "rep-2") };
        var reconciler = BuildReconciler(snapshot, out _, out _, out _, out _, out var fleetStateView);

        // Act
        await reconciler.TickAsync(CancellationToken.None);

        // Assert
        fleetStateView.Verify(v => v.Publish(snapshot), Times.Once);
    }

    // ─── AC-1: the recurring tick reads fleet-state at least once when running ─

    [Fact]
    public async Task GivenARunningReconciler_WhenStarted_ThenFleetStateIsReadAtLeastOnce()
    {
        // Arrange
        var snapshot = new[] { Row("V-001", "rep-1") };
        var reconciler = BuildReconciler(snapshot, out var apiClient, out _, out _, out _, out _);
        using var cts = new CancellationTokenSource();
        apiClient
            .Setup(c => c.GetFleetStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot)
            .Callback(() => cts.Cancel());

        // Act
        await reconciler.StartAsync(cts.Token);
        await reconciler.StopAsync(CancellationToken.None);

        // Assert
        apiClient.Verify(c => c.GetFleetStateAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task GivenARunningReconciler_WhenCancellationRequested_ThenItExitsCleanly()
    {
        // Arrange
        var snapshot = new[] { Row("V-001", "rep-1") };
        var reconciler = BuildReconciler(snapshot, out _, out _, out _, out _, out _);
        using var cts = new CancellationTokenSource();

        // Act
        var runTask = reconciler.StartAsync(cts.Token);
        await cts.CancelAsync();

        // Assert
        var exception = await Record.ExceptionAsync(() => runTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(exception);
    }
}
