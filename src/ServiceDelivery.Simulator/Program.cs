using Microsoft.Extensions.Options;
using ServiceDelivery.Simulator.Configuration;
using ServiceDelivery.Simulator.Models;
using ServiceDelivery.Simulator.Services;
using ServiceDelivery.Simulator.Workers;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        services.Configure<SimulatorOptions>(
            context.Configuration.GetSection(SimulatorOptions.SectionName));

        // The identity/session store owns authentication for the Simulator account
        // and rep1..rep8. It is a SINGLETON because it holds each identity's session
        // (token + expiry + RepId) for the lifetime of the run. It gets a named
        // HttpClient (pointed at the backend) from IHttpClientFactory.
        const string backendClientName = "Backend";
        services.AddHttpClient(backendClientName, ConfigureBackendBaseAddress);
        services.AddSingleton<IIdentitySessionStore>(sp =>
            new IdentitySessionStore(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(backendClientName),
                sp.GetRequiredService<IOptions<SimulatorOptions>>(),
                sp.GetRequiredService<ILogger<IdentitySessionStore>>()));

        // The API client attaches each identity's bearer token per request; its
        // HttpClient also needs the backend base URL.
        services.AddHttpClient<IBackendApiClient, BackendApiClient>(ConfigureBackendBaseAddress);

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

static void ConfigureBackendBaseAddress(IServiceProvider sp, HttpClient httpClient)
{
    var options = sp.GetRequiredService<IOptions<SimulatorOptions>>().Value;
    httpClient.BaseAddress = new Uri(options.BackendBaseUrl);
}
