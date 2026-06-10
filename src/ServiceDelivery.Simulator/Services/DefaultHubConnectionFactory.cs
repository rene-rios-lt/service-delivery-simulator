using Microsoft.AspNetCore.SignalR.Client;

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

    public TimeSpan[] ReconnectIntervals => DefaultReconnectIntervals;

    public HubConnection Build(string url, string jwt)
    {
        return new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(jwt);
            })
            .WithAutomaticReconnect(DefaultReconnectIntervals)
            .Build();
    }
}
