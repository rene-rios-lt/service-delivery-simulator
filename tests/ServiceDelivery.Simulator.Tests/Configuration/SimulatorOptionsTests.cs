using Microsoft.Extensions.Configuration;
using ServiceDelivery.Simulator.Configuration;
using Xunit;

namespace ServiceDelivery.Simulator.Tests.Configuration;

public class SimulatorOptionsTests
{
    private static SimulatorOptions Bind(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var options = new SimulatorOptions();
        configuration.GetSection(SimulatorOptions.SectionName).Bind(options);
        return options;
    }

    [Fact]
    public void GivenConfigWithRepAccounts_WhenSimulatorOptionsBound_ThenExposesEightRepEmailsAndRepPassword()
    {
        // Arrange
        var values = new Dictionary<string, string?>
        {
            ["Simulator:RepEmails:0"] = "rep1@dealer.com",
            ["Simulator:RepEmails:1"] = "rep2@dealer.com",
            ["Simulator:RepEmails:2"] = "rep3@dealer.com",
            ["Simulator:RepEmails:3"] = "rep4@dealer.com",
            ["Simulator:RepEmails:4"] = "rep5@dealer.com",
            ["Simulator:RepEmails:5"] = "rep6@dealer.com",
            ["Simulator:RepEmails:6"] = "rep7@dealer.com",
            ["Simulator:RepEmails:7"] = "rep8@dealer.com",
            ["Simulator:RepPassword"] = "shared-rep-password"
        };

        // Act
        var options = Bind(values);

        // Assert
        Assert.Equal(8, options.RepEmails.Length);
        Assert.Equal("rep1@dealer.com", options.RepEmails[0]);
        Assert.Equal("rep8@dealer.com", options.RepEmails[7]);
        Assert.Equal("shared-rep-password", options.RepPassword);
    }

    [Fact]
    public void GivenConfig_WhenSimulatorOptionsBound_ThenStillExposesSimulatorPositionAccount()
    {
        // Arrange
        var values = new Dictionary<string, string?>
        {
            ["Simulator:SimulatorEmail"] = "simulator@system.internal",
            ["Simulator:SimulatorPassword"] = "sim-password",
            ["Simulator:BackendBaseUrl"] = "https://localhost:5001"
        };

        // Act
        var options = Bind(values);

        // Assert
        Assert.Equal("simulator@system.internal", options.SimulatorEmail);
        Assert.Equal("sim-password", options.SimulatorPassword);
        Assert.Equal("https://localhost:5001", options.BackendBaseUrl);
    }
}
