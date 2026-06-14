namespace ServiceDelivery.Simulator.Services;

public enum IdentityRole
{
    Simulator,
    ServiceRep
}

// One operated identity. Carries the credential (Email/Password/Role) plus the
// learned session state: RepId (decoded from the JWT 'sub' claim at login for
// ServiceRep identities), the current Token, and its decoded TokenExpiry.
public sealed class RepIdentity
{
    public string Email { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public IdentityRole Role { get; init; }
    public string? RepId { get; set; }
    public string? Token { get; set; }
    public DateTime TokenExpiry { get; set; } = DateTime.MinValue;
}
