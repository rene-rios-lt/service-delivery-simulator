namespace ServiceDelivery.Simulator.Services;

// Owns the per-identity authentication session. Logs each identity in independently,
// stores one JWT per identity, and re-authenticates each identity on its own expiry.
public interface IIdentitySessionStore
{
    RepIdentity Simulator { get; }
    IReadOnlyList<RepIdentity> Reps { get; }

    Task AuthenticateAsync(RepIdentity identity, CancellationToken cancellationToken);
    Task<string> GetValidTokenAsync(RepIdentity identity, CancellationToken cancellationToken);
}
