using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ServiceDelivery.Simulator.Configuration;
using ServiceDelivery.Simulator.Services;

namespace ServiceDelivery.Simulator.Workers;

// The single per-tick fleet orchestrator (worker topology A). Once per interval it
// performs the ONE authoritative fleet-state read (AC-1), rebalances claims, then for
// each vehicle resolves a drive mode and drives its position (AC-2) and runs the
// auto-decision engine only for reps no human controls (AC-3). Per-decision policy
// and navigate/hold geometry live in the injected collaborators, not here.
public sealed class FleetReconciler : BackgroundService
{
    private readonly IBackendApiClient _apiClient;
    private readonly IFleetClaimCoordinator _claimCoordinator;
    private readonly IVehicleDriveResolver _driveResolver;
    private readonly IRepOperationGate _operationGate;
    private readonly IVehiclePositionDriver _positionDriver;
    private readonly IAutoDecisionEngine _autoDecisionEngine;
    private readonly IFleetStateView _fleetStateView;
    private readonly IArrivalReporter _arrivalReporter;
    private readonly ISignalRClient _signalRClient;
    private readonly IYieldedRepRegistry _yieldedReps;
    private readonly TimeSpan _tickInterval;
    private readonly ILogger<FleetReconciler> _logger;

    public FleetReconciler(
        IBackendApiClient apiClient,
        IFleetClaimCoordinator claimCoordinator,
        IVehicleDriveResolver driveResolver,
        IRepOperationGate operationGate,
        IVehiclePositionDriver positionDriver,
        IAutoDecisionEngine autoDecisionEngine,
        IFleetStateView fleetStateView,
        IArrivalReporter arrivalReporter,
        ISignalRClient signalRClient,
        IYieldedRepRegistry yieldedReps,
        IOptions<SimulatorOptions> options,
        ILogger<FleetReconciler> logger)
    {
        _apiClient = apiClient;
        _claimCoordinator = claimCoordinator;
        _driveResolver = driveResolver;
        _operationGate = operationGate;
        _positionDriver = positionDriver;
        _autoDecisionEngine = autoDecisionEngine;
        _fleetStateView = fleetStateView;
        _arrivalReporter = arrivalReporter;
        _signalRClient = signalRClient;
        _yieldedReps = yieldedReps;
        _tickInterval = TimeSpan.FromSeconds(options.Value.PositionUpdateIntervalSeconds);
        _logger = logger;
    }

    public async Task TickAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _apiClient.GetFleetStateAsync(cancellationToken);

        // SIM-005: publish the latest snapshot so the offer-triggered decision engine
        // can resolve each rep's human-control state when an offer arrives between ticks.
        _fleetStateView.Publish(snapshot);

        await _claimCoordinator.RebalanceAsync(snapshot, cancellationToken);

        foreach (var row in snapshot)
        {
            var mode = _driveResolver.Resolve(row);
            await _positionDriver.DriveAsync(row, mode, cancellationToken);

            // SIM-009: record a yield the moment a row is first seen human-controlled,
            // AFTER position drive (which is never gated) and BEFORE the operation gate
            // so the gate's read reflects this tick's observation. Sticky for the run.
            _yieldedReps.ObserveAndRecordIfYielded(row);

            if (_operationGate.ShouldOperate(row))
            {
                // BUG-053: heal any deaf RepHub connection for this operated rep within
                // one tick, so a silently-dead websocket cannot let offers expire.
                if (row.ClaimingRepId is not null)
                    await _signalRClient.EnsureConnectedAsync(row.ClaimingRepId, cancellationToken);

                await _autoDecisionEngine.RunAsync(row, cancellationToken);

                // SIM-006: arrival reporting is gated automated-only on the same flag,
                // so it never fires for a human-controlled rep.
                await _arrivalReporter.ReportArrivalIfReachedAsync(row, cancellationToken);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("FleetReconciler starting; tick interval {Interval}.", _tickInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fleet reconciliation tick failed. Will retry on next tick.");
            }

            try
            {
                await Task.Delay(_tickInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
