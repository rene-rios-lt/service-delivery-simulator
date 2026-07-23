namespace ServiceDelivery.Simulator.Models;

public sealed record JobOfferPayload(
    string OfferId,
    string RequestId,
    string RequesterName,
    string RequesterTier,
    string DtcTitle,
    double Latitude,
    double Longitude,
    double DistanceMiles,
    double EtaMinutes);
