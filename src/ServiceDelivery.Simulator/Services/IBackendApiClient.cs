using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

public interface IBackendApiClient
{
    string? StoredJwt { get; }
    Task AuthenticateAsync(CancellationToken cancellationToken);
    Task PostPositionAsync(VehiclePosition position, CancellationToken cancellationToken);
    Task AcceptJobOfferAsync(string offerId, CancellationToken cancellationToken);
    Task DeclineJobOfferAsync(string offerId, CancellationToken cancellationToken);
}
