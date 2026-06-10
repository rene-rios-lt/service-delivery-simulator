using Microsoft.AspNetCore.SignalR.Client;

namespace ServiceDelivery.Simulator.Services;

public interface IHubConnectionFactory
{
    HubConnection Build(string url, string jwt);
}
