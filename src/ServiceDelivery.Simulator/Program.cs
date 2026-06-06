using ServiceDelivery.Simulator.Configuration;
using ServiceDelivery.Simulator.Services;
using ServiceDelivery.Simulator.Workers;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        services.Configure<SimulatorOptions>(
            context.Configuration.GetSection(SimulatorOptions.SectionName));

        services.AddHttpClient<BackendApiClient>();
        services.AddSingleton<SignalRClient>();

        for (int vehicleIndex = 0; vehicleIndex < 8; vehicleIndex++)
        {
            var index = vehicleIndex;
            services.AddSingleton<IHostedService>(sp =>
                new VehicleWorker(
                    index,
                    sp.GetRequiredService<BackendApiClient>(),
                    sp.GetRequiredService<ILogger<VehicleWorker>>()));
        }
    })
    .Build();

await host.RunAsync();
