using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceDelivery.Simulator.Models;
using ServiceDelivery.Simulator.Services;
using Xunit;

namespace ServiceDelivery.Simulator.Tests.Services;

// SIM-006: the automated-only arrive handoff. For an EnRoute/Within15Miles automated rep
// whose truck has reached the requester, ReportArrivalIfReachedAsync calls
// IBackendApiClient.ArriveAsync exactly once. It is gated by RepOperationGate so it never
// fires for a human-controlled rep, and it is idempotent per arrival (no double-arrive).
public class ArrivalReporterTests
{
    private const string VehicleId = "V-1";
    private const string RepId = "rep-1";
    private static readonly RequesterLocation Target = new(41.5, -93.6);

    private static RepIdentity Rep() =>
        new() { Email = "rep1@dealer.com", Password = "pw", Role = IdentityRole.ServiceRep, RepId = RepId, Token = "tok" };

    private static FleetStateRow Row(bool humanControlled = false, RepState state = RepState.EnRoute,
        RequesterLocation? location = null) =>
        new(VehicleId, RepId, state, humanControlled, location ?? Target);

    private static Mock<IIdentitySessionStore> StoreWithRep()
    {
        var store = new Mock<IIdentitySessionStore>();
        store.Setup(s => s.Reps).Returns(new[] { Rep() });
        return store;
    }

    private static Mock<IVehiclePositionProvider> ProviderAt(double lat, double lng, bool known = true)
    {
        var provider = new Mock<IVehiclePositionProvider>();
        var pos = (lat, lng);
        provider
            .Setup(p => p.TryGetPosition(VehicleId, out It.Ref<(double Lat, double Lng)>.IsAny))
            .Returns(new TryGetPositionCallback((string _, out (double Lat, double Lng) p) =>
            {
                p = pos;
                return known;
            }));
        return provider;
    }

    private delegate bool TryGetPositionCallback(string vehicleId, out (double Lat, double Lng) position);

    private static ArrivalReporter BuildReporter(
        Mock<IBackendApiClient> api, Mock<IVehiclePositionProvider> provider, Mock<IIdentitySessionStore> store) =>
        new(api.Object, new StraightLineNavigator(), provider.Object,
            new RepOperationGate(new YieldedRepRegistry()), store.Object, NullLogger<ArrivalReporter>.Instance);

    // ─── AC-2: reached + automated → arrive exactly once ──────────────────────

    [Fact]
    public async Task GivenAnAutomatedRepWhoseTruckHasReachedRequester_WhenReportArrivalIfReached_ThenArriveAsyncCalledOnce()
    {
        // Arrange — truck sitting exactly on the requester
        var api = new Mock<IBackendApiClient>();
        var provider = ProviderAt(Target.Lat, Target.Lng);
        var reporter = BuildReporter(api, provider, StoreWithRep());

        // Act
        await reporter.ReportArrivalIfReachedAsync(Row(), CancellationToken.None);

        // Assert
        api.Verify(c => c.ArriveAsync(It.Is<RepIdentity>(r => r.RepId == RepId), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── AC-2: not yet reached → no arrive ────────────────────────────────────

    [Fact]
    public async Task GivenAnAutomatedRepNotYetReached_WhenReportArrivalIfReached_ThenArriveAsyncNotCalled()
    {
        // Arrange — truck ~1.1 km away (well beyond the arrival threshold)
        var api = new Mock<IBackendApiClient>();
        var provider = ProviderAt(Target.Lat + 0.01, Target.Lng);
        var reporter = BuildReporter(api, provider, StoreWithRep());

        // Act
        await reporter.ReportArrivalIfReachedAsync(Row(), CancellationToken.None);

        // Assert
        api.Verify(c => c.ArriveAsync(It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── AC-2: already arrived → no double-arrive ─────────────────────────────

    [Fact]
    public async Task GivenAnAutomatedRepAlreadyArrived_WhenReportArrivalIfReachedAgain_ThenArriveAsyncNotCalledTwice()
    {
        // Arrange
        var api = new Mock<IBackendApiClient>();
        var provider = ProviderAt(Target.Lat, Target.Lng);
        var reporter = BuildReporter(api, provider, StoreWithRep());

        // Act — report twice for the same reached arrival
        await reporter.ReportArrivalIfReachedAsync(Row(), CancellationToken.None);
        await reporter.ReportArrivalIfReachedAsync(Row(), CancellationToken.None);

        // Assert
        api.Verify(c => c.ArriveAsync(It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── AC-3: human-controlled rep → never arrive, even when reached ─────────

    [Fact]
    public async Task GivenAHumanControlledRepReachedRequester_WhenReportArrivalIfReached_ThenArriveAsyncNeverCalled()
    {
        // Arrange — truck on the requester but the rep is human-controlled
        var api = new Mock<IBackendApiClient>();
        var provider = ProviderAt(Target.Lat, Target.Lng);
        var reporter = BuildReporter(api, provider, StoreWithRep());

        // Act
        await reporter.ReportArrivalIfReachedAsync(Row(humanControlled: true), CancellationToken.None);

        // Assert
        api.Verify(c => c.ArriveAsync(It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
