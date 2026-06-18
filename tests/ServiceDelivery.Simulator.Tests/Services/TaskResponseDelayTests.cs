using ServiceDelivery.Simulator.Services;
using Xunit;

namespace ServiceDelivery.Simulator.Tests.Services;

public class TaskResponseDelayTests
{
    [Fact]
    public async Task GivenAZeroDuration_WhenDelayAsync_ThenItCompletes()
    {
        // Arrange
        var delay = new TaskResponseDelay();

        // Act
        var exception = await Record.ExceptionAsync(() => delay.DelayAsync(TimeSpan.Zero, CancellationToken.None));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public async Task GivenAnAlreadyCancelledToken_WhenDelayAsync_ThenItHonoursCancellation()
    {
        // Arrange
        var delay = new TaskResponseDelay();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => delay.DelayAsync(TimeSpan.FromSeconds(5), cts.Token));
    }
}
