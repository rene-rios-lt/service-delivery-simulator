using ServiceDelivery.Simulator.Configuration;
using ServiceDelivery.Simulator.Models;
using ServiceDelivery.Simulator.Services;
using ServiceDelivery.Simulator.Workers;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        services.Configure<SimulatorOptions>(
            context.Configuration.GetSection(SimulatorOptions.SectionName));

        services.AddHttpClient<IBackendApiClient, BackendApiClient>();
        services.AddSingleton<IHubConnectionFactory, DefaultHubConnectionFactory>();
        services.AddSingleton<ISignalRClient, SignalRClient>();

        // SimulatorStartupService must be registered first — it authenticates and
        // connects SignalR before any VehicleWorker begins. Hosted services start
        // in registration order.
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

await host.RunAsync();
