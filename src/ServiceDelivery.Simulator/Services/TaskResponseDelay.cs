namespace ServiceDelivery.Simulator.Services;

// Production delay: delegates to Task.Delay so the configured pause and cancellation
// are honoured in real time.
public sealed class TaskResponseDelay : IResponseDelay
{
    public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken) =>
        Task.Delay(duration, cancellationToken);
}
