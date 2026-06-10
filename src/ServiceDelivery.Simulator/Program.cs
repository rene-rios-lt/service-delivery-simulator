using ServiceDelivery.Simulator.Configuration;
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

        for (int vehicleIndex = 0; vehicleIndex < 8; vehicleIndex++)
        {
            var index = vehicleIndex;
            services.AddSingleton<IHostedService>(sp =>
                new VehicleWorker(
                    index,
                    sp.GetRequiredService<IBackendApiClient>(),
                    sp.GetRequiredService<ILogger<VehicleWorker>>()));
        }
    })
    .Build();

await host.RunAsync();
