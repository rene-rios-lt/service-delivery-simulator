namespace ServiceDelivery.Simulator.Services;

// Delay seam for the simulated "rep reviewing the offer" pause. Injecting it lets
// tests assert the delay was requested with the expected duration without waiting in
// real time.
public interface IResponseDelay
{
    Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken);
}
