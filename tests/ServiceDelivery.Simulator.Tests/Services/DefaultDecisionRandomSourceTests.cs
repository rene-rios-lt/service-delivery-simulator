using ServiceDelivery.Simulator.Services;
using Xunit;

namespace ServiceDelivery.Simulator.Tests.Services;

// The production RNG must stay inside the documented bounds so the threshold rule and
// the 1-5s delay are meaningful. Run many iterations to exercise the range.
public class DefaultDecisionRandomSourceTests
{
    [Fact]
    public void GivenTheSource_WhenNextPercentCalledManyTimes_ThenEveryValueIsBetween0And99()
    {
        // Arrange
        var source = new DefaultDecisionRandomSource();

        // Act
        var values = Enumerable.Range(0, 1000).Select(_ => source.NextPercent()).ToList();

        // Assert
        Assert.All(values, v => Assert.InRange(v, 0, 99));
    }

    [Fact]
    public void GivenTheSource_WhenNextDelaySecondsCalledManyTimes_ThenEveryValueIsWithinTheInclusiveBounds()
    {
        // Arrange
        var source = new DefaultDecisionRandomSource();

        // Act
        var values = Enumerable.Range(0, 1000).Select(_ => source.NextDelaySeconds(1, 5)).ToList();

        // Assert
        Assert.All(values, v => Assert.InRange(v, 1, 5));
    }
}
