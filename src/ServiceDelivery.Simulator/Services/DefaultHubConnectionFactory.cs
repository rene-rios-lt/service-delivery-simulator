using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace ServiceDelivery.Simulator.Services;

public sealed class DefaultHubConnectionFactory : IHubConnectionFactory
{
    public static readonly TimeSpan[] DefaultReconnectIntervals =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30)
    ];

    private readonly ILoggerFactory _loggerFactory;

    public DefaultHubConnectionFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public TimeSpan[] ReconnectIntervals => DefaultReconnectIntervals;

    public HubConnection Build(string url, string jwt)
    {
        return new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(jwt);
            })
            .WithAutomaticReconnect(DefaultReconnectIntervals)
            .ConfigureLogging(logging => logging.AddProvider(new ForwardingLoggerProvider(_loggerFactory)))
            .Build();
    }

    // Routes the hub client's own lifecycle logging (connection state, transport
    // errors, reconnect attempts) into the host's already-configured ILoggerFactory
    // instead of the HubConnection's default null pipeline — so those lines land in
    // the same sinks as the rest of the simulator rather than silently vanishing.
    // A thin ILoggerProvider adapter is the only way to feed an existing ILoggerFactory
    // into HubConnectionBuilder.ConfigureLogging (which otherwise builds its own).
    private sealed class ForwardingLoggerProvider(ILoggerFactory inner) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => inner.CreateLogger(categoryName);

        public void Dispose()
        {
            // The host owns the wrapped factory's lifetime; do not dispose it here.
        }
    }
}
