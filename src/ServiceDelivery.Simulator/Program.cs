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

        // SIM-008 reconciliation collaborators. The reconciler is the single per-tick
        // orchestrator (topology A); the resolver/gate are pure decisions; the claim
        // coordinator owns startup + rebalance; the auto-decision policy is a SIM-005
        // seam wired here as a logging placeholder.
        services.AddSingleton<IVehicleDriveResolver, VehicleDriveResolver>();
        services.AddSingleton<IRepOperationGate, RepOperationGate>();
        services.AddSingleton<IAutoDecisionEngine, LoggingAutoDecisionEngine>();
        services.AddSingleton<IFleetClaimCoordinator, FleetClaimCoordinator>();

        // SIM-005 offer-triggered auto-response. IFleetStateView is a SINGLETON shared
        // between the FleetReconciler (writer, once per tick) and the decision engine
        // (reader, on the SignalR handler thread) — its implementation is thread-safe.
        services.AddSingleton<IFleetStateView, FleetStateView>();
        services.AddSingleton<IDecisionRandomSource, DefaultDecisionRandomSource>();
        services.AddSingleton<IResponseDelay, TaskResponseDelay>();
        services.AddSingleton<IJobOfferDecisionEngine, JobOfferDecisionEngine>();

        // One VehicleWorker drive object per vehicle, fronted by the fleet-wide
        // position driver the reconciler depends on.
        foreach (var route in IowaRoutes.All)
        {
            var capturedRoute = route;
            services.AddSingleton(sp =>
                new VehicleWorker(
                    capturedRoute,
                    sp.GetRequiredService<IBackendApiClient>(),
                    sp.GetRequiredService<ILogger<VehicleWorker>>()));
        }

        services.AddSingleton<IVehiclePositionDriver, FleetPositionDriver>();

        // SimulatorStartupService must be registered first — it authenticates, connects
        // SignalR, and performs the startup claim before the FleetReconciler ticks.
        // Hosted services start in registration order.
        services.AddHostedService<SimulatorStartupService>();
        services.AddHostedService<FleetReconciler>();
    })
    .Build();

await host.RunAsync();

static void ConfigureBackendBaseAddress(IServiceProvider sp, HttpClient httpClient)
{
    var options = sp.GetRequiredService<IOptions<SimulatorOptions>>().Value;
    httpClient.BaseAddress = new Uri(options.BackendBaseUrl);
}
