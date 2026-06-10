using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceDelivery.Simulator.Configuration;
using ServiceDelivery.Simulator.Models;
using ServiceDelivery.Simulator.Services;
using ServiceDelivery.Simulator.Workers;
using Xunit;

namespace ServiceDelivery.Simulator.Tests.Workers;

public class VehicleWorkerRegistrationTests
{
    [Fact]
    public void GivenSimulatorHost_WhenBuilt_ThenEightVehicleWorkersAreRegistered()
    {
        // Arrange
        var host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Simulator:BackendBaseUrl"] = "https://localhost:5001",
                    ["Simulator:SimulatorEmail"] = "simulator@system.internal",
                    ["Simulator:SimulatorPassword"] = "test-password"
                });
            })
            .ConfigureServices((context, services) =>
            {
                services.Configure<SimulatorOptions>(
                    context.Configuration.GetSection(SimulatorOptions.SectionName));

                services.AddHttpClient<IBackendApiClient, BackendApiClient>();
                services.AddSingleton<IHubConnectionFactory, DefaultHubConnectionFactory>();
                services.AddSingleton<ISignalRClient, SignalRClient>();

                services.AddHostedService<SimulatorStartupService>();

                foreach (var route in IowaRoutes.All)
                {
                    var capturedRoute = route;
                    services.AddSingleton<IHostedService>(sp =>
                        new VehicleWorker(
                            capturedRoute,
                            sp.GetRequiredService<IBackendApiClient>(),
                            sp.GetRequiredService<ILogger<VehicleWorker>>()));
                }
            })
            .Build();

        // Act
        var hostedServices = host.Services.GetServices<IHostedService>().ToList();
        var vehicleWorkers = hostedServices.OfType<VehicleWorker>().ToList();

        // Assert
        Assert.Equal(8, vehicleWorkers.Count);
    }
}
