using ServiceDelivery.Simulator.Models;
using ServiceDelivery.Simulator.Services;
using Xunit;

namespace ServiceDelivery.Simulator.Tests.Services;

// AC-1 plumbing: the offer-triggered decision engine resolves a rep's latest known
// human-control state by looking the rep up in the most-recent published snapshot.
public class FleetStateViewTests
{
    private static FleetStateRow Row(string repId, bool humanControlled = false) =>
        new("V-001", repId, RepState.Available, humanControlled, null);

    [Fact]
    public void GivenAPublishedSnapshot_WhenTryGetByRepId_ThenTheMatchingRowIsReturned()
    {
        // Arrange
        var view = new FleetStateView();
        var rowForRep1 = Row("rep-1");
        var rowForRep2 = Row("rep-2");
        view.Publish(new[] { rowForRep1, rowForRep2 });

        // Act
        var found = view.TryGetByRepId("rep-2", out var row);

        // Assert
        Assert.True(found);
        Assert.Equal(rowForRep2, row);
    }

    [Fact]
    public void GivenNoSnapshotForARep_WhenTryGetByRepId_ThenReturnsFalse()
    {
        // Arrange
        var view = new FleetStateView();
        view.Publish(new[] { Row("rep-1") });

        // Act
        var found = view.TryGetByRepId("rep-unknown", out var row);

        // Assert
        Assert.False(found);
        Assert.Null(row);
    }
}
