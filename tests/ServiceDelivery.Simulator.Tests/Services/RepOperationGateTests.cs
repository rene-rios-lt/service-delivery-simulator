using ServiceDelivery.Simulator.Models;
using ServiceDelivery.Simulator.Services;
using Xunit;

namespace ServiceDelivery.Simulator.Tests.Services;

// AC-3: the auto-decision engine operates a rep only when no human controls it.
// Sticky-yield memory is SIM-009; this gate honours only the live human-controlled flag.
public class RepOperationGateTests
{
    private static FleetStateRow Row(bool humanControlled) =>
        new("V-001", "rep-1", RepState.Available, humanControlled, ActiveRequestLocation: null);

    private readonly RepOperationGate _gate = new();

    [Fact]
    public void GivenANonHumanControlledRep_WhenGated_ThenShouldOperateIsTrue()
    {
        // Arrange
        var row = Row(humanControlled: false);

        // Act
        var shouldOperate = _gate.ShouldOperate(row);

        // Assert
        Assert.True(shouldOperate);
    }

    [Fact]
    public void GivenAHumanControlledRep_WhenGated_ThenShouldOperateIsFalse()
    {
        // Arrange
        var row = Row(humanControlled: true);

        // Act
        var shouldOperate = _gate.ShouldOperate(row);

        // Assert
        Assert.False(shouldOperate);
    }
}
