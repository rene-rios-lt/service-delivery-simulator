using ServiceDelivery.Simulator.Services;
using Xunit;

namespace ServiceDelivery.Simulator.Tests.Services;

// QUAL-029 AC-1: the per-rep "one offer at a time" latch. A rep holds at most one live
// offer; a second offer that arrives while one is in flight must find the gate held.
public class LiveOfferGateTests
{
    private const string RepId = "rep-1";

    [Fact]
    public void GivenAFreeGate_WhenTryAcquireCalled_ThenReturnsTrueAndGateIsHeld()
    {
        // Arrange
        var gate = new LiveOfferGate();

        // Act
        var acquired = gate.TryAcquire(RepId);

        // Assert
        Assert.True(acquired);
    }

    [Fact]
    public void GivenAHeldGate_WhenTryAcquireCalledForSameRep_ThenReturnsFalse()
    {
        // Arrange
        var gate = new LiveOfferGate();
        gate.TryAcquire(RepId);

        // Act
        var secondAcquire = gate.TryAcquire(RepId);

        // Assert
        Assert.False(secondAcquire);
    }

    [Fact]
    public void GivenAHeldGateThenReleased_WhenTryAcquireCalled_ThenReturnsTrue()
    {
        // Arrange
        var gate = new LiveOfferGate();
        gate.TryAcquire(RepId);
        gate.Release(RepId);

        // Act
        var reacquired = gate.TryAcquire(RepId);

        // Assert
        Assert.True(reacquired);
    }

    [Fact]
    public void GivenGateHeldForOneRep_WhenTryAcquireCalledForDifferentRep_ThenReturnsTrue()
    {
        // Arrange
        var gate = new LiveOfferGate();
        gate.TryAcquire("rep-1");

        // Act
        var otherRepAcquire = gate.TryAcquire("rep-2");

        // Assert
        Assert.True(otherRepAcquire);
    }
}
