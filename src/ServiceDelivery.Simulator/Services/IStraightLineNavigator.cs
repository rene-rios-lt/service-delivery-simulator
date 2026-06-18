using ServiceDelivery.Simulator.Models;

namespace ServiceDelivery.Simulator.Services;

// Pure navigation geometry. Given a current position and a target, returns the next
// position one bounded straight-line step toward the target and whether the target has
// now been reached. No HTTP, no state — the single home for the per-tick step distance
// and arrival-threshold constants so the worker and the arrival reporter agree.
public interface IStraightLineNavigator
{
    NavigationStep Step(double currentLat, double currentLng, double targetLat, double targetLng);

    bool HasReached(double currentLat, double currentLng, double targetLat, double targetLng);
}
