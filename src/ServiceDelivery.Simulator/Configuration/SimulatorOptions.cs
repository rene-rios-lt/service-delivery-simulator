namespace ServiceDelivery.Simulator.Configuration;

public sealed class SimulatorOptions
{
    public const string SectionName = "Simulator";

    public string BackendBaseUrl { get; init; } = string.Empty;
    public int PositionUpdateIntervalSeconds { get; init; } = 3;
    public int AutoDeclineRatePercent { get; init; } = 15;
    public int OnSiteDelaySeconds { get; init; } = 30;
    public string SimulatorEmail { get; init; } = string.Empty;
    public string SimulatorPassword { get; init; } = string.Empty;
}
