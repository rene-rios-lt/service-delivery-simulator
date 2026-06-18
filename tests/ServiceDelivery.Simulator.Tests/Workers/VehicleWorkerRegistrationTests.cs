using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ServiceDelivery.Simulator.Configuration;
using ServiceDelivery.Simulator.Models;
using ServiceDelivery.Simulator.Services;
using ServiceDelivery.Simulator.Workers;
using Xunit;

namespace ServiceDelivery.Simulator.Tests.Workers;

// SIM-008 topology A: the FleetReconciler is the single per-tick hosted orchestrator,
// and there is one VehicleWorker drive object per vehicle (all 8 still driven — the
// intent of the original SIM-004 registration test is preserved while the mechanism
// changes from 8 BackgroundServices to 8 drive objects behind one FleetPositionDriver).
public class VehicleWorkerRegistrationTests
{
    private static IHost BuildHost() =>
        Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Simulator:BackendBaseUrl"] = "https://localhost:5001",
                    ["Simulator:SimulatorEmail"] = "simulator@system.internal",
                    ["Simulator:SimulatorPassword"] = "test-password",
                    ["Simulator:RepEmails:0"] = "rep1@dealer.com",
                    ["Simulator:RepEmails:1"] = "rep2@dealer.com",
                    ["Simulator:RepPassword"] = "rep-password"
                });
            })
            .ConfigureServices((context, services) =>
            {
                services.Configure<SimulatorOptions>(
                    context.Configuration.GetSection(SimulatorOptions.SectionName));

                services.AddHttpClient<IBackendApiClient, BackendApiClient>();
                services.AddSingleton<IIdentitySessionStore>(sp =>
                    new IdentitySessionStore(
                        sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
                        sp.GetRequiredService<IOptions<SimulatorOptions>>(),
                        sp.GetRequiredService<ILogger<IdentitySessionStore>>()));
                services.AddSingleton<IHubConnectionFactory, DefaultHubConnectionFactory>();
                services.AddSingleton<ISignalRClient, SignalRClient>();

                services.AddSingleton<IYieldedRepRegistry, YieldedRepRegistry>();
                services.AddSingleton<IVehicleDriveResolver, VehicleDriveResolver>();
                services.AddSingleton<IRepOperationGate, RepOperationGate>();
                services.AddSingleton<IStraightLineNavigator, StraightLineNavigator>();
                services.AddSingleton<IAutoDecisionEngine, LoggingAutoDecisionEngine>();
                services.AddSingleton<IFleetClaimCoordinator, FleetClaimCoordinator>();

                // SIM-005 collaborators the FleetReconciler / decision engine depend on.
                services.AddSingleton<IFleetStateView, FleetStateView>();
                services.AddSingleton<IDecisionRandomSource, DefaultDecisionRandomSource>();
                services.AddSingleton<IResponseDelay, TaskResponseDelay>();
                services.AddSingleton<IJobOfferDecisionEngine, JobOfferDecisionEngine>();

                foreach (var route in IowaRoutes.All)
                {
                    var capturedRoute = route;
                    services.AddSingleton(sp =>
                        new VehicleWorker(
                            capturedRoute,
                            sp.GetRequiredService<IBackendApiClient>(),
                            sp.GetRequiredService<IStraightLineNavigator>(),
                            sp.GetRequiredService<ILogger<VehicleWorker>>()));
                }

                services.AddSingleton<FleetPositionDriver>();
                services.AddSingleton<IVehiclePositionDriver>(sp => sp.GetRequiredService<FleetPositionDriver>());
                services.AddSingleton<IVehiclePositionProvider>(sp => sp.GetRequiredService<FleetPositionDriver>());
                services.AddSingleton<IArrivalReporter, ArrivalReporter>();

                services.AddHostedService<FleetReconciler>();
            })
            .Build();

    [Fact]
    public void GivenSimulatorHost_WhenBuilt_ThenEightVehicleWorkersAreRegistered()
    {
        // Arrange
        using var host = BuildHost();

        // Act
        var vehicleWorkers = host.Services.GetServices<VehicleWorker>().ToList();

        // Assert
        Assert.Equal(8, vehicleWorkers.Count);
    }

    [Fact]
    public void GivenSimulatorHost_WhenBuilt_ThenASingleFleetReconcilerOrchestratorIsRegistered()
    {
        // Arrange
        using var host = BuildHost();

        // Act
        var reconcilers = host.Services.GetServices<IHostedService>().OfType<FleetReconciler>().ToList();

        // Assert
        Assert.Single(reconcilers);
    }
}
