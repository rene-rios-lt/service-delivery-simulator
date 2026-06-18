using ServiceDelivery.Simulator.Models;
using ServiceDelivery.Simulator.Services;
using Xunit;

namespace ServiceDelivery.Simulator.Tests.Services;

// AC-3: the auto-decision engine operates a rep only when no human controls it.
// Sticky-yield memory is SIM-009; this gate honours only the live human-controlled flag.
public class RepOperationGateTests
{
    private static FleetStateRow Row(bool humanControlled, string? repId = "rep-1") =>
        new("V-001", repId, RepState.Available, humanControlled, ActiveRequestLocation: null);

    private readonly YieldedRepRegistry _registry = new();
    private readonly RepOperationGate _gate;

    public RepOperationGateTests()
    {
        _gate = new RepOperationGate(_registry);
    }

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

    [Fact]
    public void GivenAYieldedRep_WhenGatedWithLiveFlagFalse_ThenShouldOperateIsFalse()
    {
        // Arrange — the rep was previously observed human-controlled, then went off-duty
        // so the current row's live flag is false but ClaimingRepId is still present
        _registry.ObserveAndRecordIfYielded(Row(humanControlled: true, repId: "rep-1"));
        var offDutyRow = Row(humanControlled: false, repId: "rep-1");

        // Act
        var shouldOperate = _gate.ShouldOperate(offDutyRow);

        // Assert — sticky yield keeps the rep non-operable even though the flag cleared
        Assert.False(shouldOperate);
    }

    [Fact]
    public void GivenAYieldedVehicleWhoseRowHasClearedClaimingRep_WhenGated_ThenShouldOperateIsFalse()
    {
        // Arrange — the rep parked the truck off-duty: ClaimingRepId is now null, but
        // the vehicle (V-001) was recorded yielded while it was still human-controlled
        _registry.ObserveAndRecordIfYielded(Row(humanControlled: true, repId: "rep-1"));
        var offDutyRow = Row(humanControlled: false, repId: null);

        // Act
        var shouldOperate = _gate.ShouldOperate(offDutyRow);

        // Assert — the vehicle is still yielded, so the row is never re-operated
        Assert.False(shouldOperate);
    }
}
