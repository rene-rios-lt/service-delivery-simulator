using ServiceDelivery.Simulator.Models;
using ServiceDelivery.Simulator.Services;
using Xunit;

namespace ServiceDelivery.Simulator.Tests.Services;

// SIM-009 AC-3/AC-4: the sticky-exclusion registry records (repId, vehicleId) the
// moment a row is first observed human-controlled and never clears it for the run.
public class YieldedRepRegistryTests
{
    private static FleetStateRow Row(
        string vehicleId = "V-001", string? repId = "rep-1", bool humanControlled = true) =>
        new(vehicleId, repId, RepState.Available, humanControlled, ActiveRequestLocation: null);

    [Fact]
    public void GivenAHumanControlledRow_WhenObserved_ThenRepAndVehicleAreYielded()
    {
        // Arrange
        var registry = new YieldedRepRegistry();
        var row = Row("V-001", "rep-1", humanControlled: true);

        // Act
        registry.ObserveAndRecordIfYielded(row);

        // Assert
        Assert.True(registry.IsRepYielded("rep-1"));
        Assert.True(registry.IsVehicleYielded("V-001"));
    }

    [Fact]
    public void GivenAYieldedRep_WhenLaterObservedNotHumanControlled_ThenRepRemainsYielded()
    {
        // Arrange — first tick observes the rep human-controlled
        var registry = new YieldedRepRegistry();
        registry.ObserveAndRecordIfYielded(Row("V-001", "rep-1", humanControlled: true));

        // Act — a later tick observes the same rep/vehicle gone off-duty (not human-controlled)
        registry.ObserveAndRecordIfYielded(
            new FleetStateRow("V-001", ClaimingRepId: null, RepState.Offline, HumanControlled: false, ActiveRequestLocation: null));

        // Assert — the original yield is sticky and never cleared
        Assert.True(registry.IsRepYielded("rep-1"));
        Assert.True(registry.IsVehicleYielded("V-001"));
    }

    [Fact]
    public void GivenAYieldedVehicle_WhenQueried_ThenIsVehicleYieldedIsTrue()
    {
        // Arrange — observe a human-controlled vehicle for rep-1
        var registry = new YieldedRepRegistry();
        registry.ObserveAndRecordIfYielded(Row("V-009", "rep-1", humanControlled: true));

        // Act
        var vehicleYielded = registry.IsVehicleYielded("V-009");

        // Assert — the vehicle is excluded independently of any particular rep id
        Assert.True(vehicleYielded);
        Assert.False(registry.IsVehicleYielded("V-010"));
        Assert.False(registry.IsRepYielded("rep-2"));
    }

    [Fact]
    public void GivenANonHumanControlledRow_WhenObserved_ThenNothingIsYielded()
    {
        // Arrange
        var registry = new YieldedRepRegistry();
        var row = Row("V-001", "rep-1", humanControlled: false);

        // Act
        registry.ObserveAndRecordIfYielded(row);

        // Assert
        Assert.False(registry.IsRepYielded("rep-1"));
        Assert.False(registry.IsVehicleYielded("V-001"));
    }
}
