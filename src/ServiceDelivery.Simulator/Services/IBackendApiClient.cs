using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

public interface IBackendApiClient
{
    Task PostPositionAsync(VehiclePosition position, CancellationToken cancellationToken);
    Task AcceptJobOfferAsync(string offerId, RepIdentity rep, CancellationToken cancellationToken);
    Task DeclineJobOfferAsync(string offerId, RepIdentity rep, CancellationToken cancellationToken);
}
