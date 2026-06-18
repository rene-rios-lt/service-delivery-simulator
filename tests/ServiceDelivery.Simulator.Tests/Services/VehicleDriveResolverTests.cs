using ServiceDelivery.Simulator.Models;
using ServiceDelivery.Simulator.Services;
using Xunit;

namespace ServiceDelivery.Simulator.Tests.Services;

// AC-2: a fleet-state row maps to exactly one drive mode. Available/Offline or no
// active request → IdleLoop; EnRoute/Within15Miles with a location → Navigate;
// OnSite → Hold. Navigate/Hold geometry itself is SIM-006 — this only selects mode.
public class VehicleDriveResolverTests
{
    private static FleetStateRow Row(RepState state, RequesterLocation? location) =>
        new("V-001", "rep-1", state, HumanControlled: false, location);

    private readonly VehicleDriveResolver _resolver = new();

    [Fact]
    public void GivenAnAvailableRepWithNoActiveRequest_WhenResolved_ThenDriveModeIsIdleLoop()
    {
        // Arrange
        var row = Row(RepState.Available, location: null);

        // Act
        var mode = _resolver.Resolve(row);

        // Assert
        Assert.Equal(VehicleDriveMode.IdleLoop, mode);
    }

    [Theory]
    [InlineData(RepState.EnRoute)]
    [InlineData(RepState.Within15Miles)]
    public void GivenAnEnRouteRepWithActiveRequestLocation_WhenResolved_ThenDriveModeIsNavigate(RepState state)
    {
        // Arrange
        var row = Row(state, new RequesterLocation(41.6, -93.7));

        // Act
        var mode = _resolver.Resolve(row);

        // Assert
        Assert.Equal(VehicleDriveMode.Navigate, mode);
    }

    [Fact]
    public void GivenAnOnSiteRep_WhenResolved_ThenDriveModeIsHold()
    {
        // Arrange
        var row = Row(RepState.OnSite, new RequesterLocation(41.6, -93.7));

        // Act
        var mode = _resolver.Resolve(row);

        // Assert
        Assert.Equal(VehicleDriveMode.Hold, mode);
    }

    [Theory]
    [InlineData(RepState.Offline)]
    [InlineData(RepState.Available)]
    public void GivenAnIdleRepStateWithNoActiveRequest_WhenResolved_ThenDriveModeIsIdleLoop(RepState state)
    {
        // Arrange
        var row = Row(state, location: null);

        // Act
        var mode = _resolver.Resolve(row);

        // Assert
        Assert.Equal(VehicleDriveMode.IdleLoop, mode);
    }
}
