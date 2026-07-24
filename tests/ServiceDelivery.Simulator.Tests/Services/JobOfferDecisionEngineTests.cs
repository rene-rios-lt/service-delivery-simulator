using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ServiceDelivery.Simulator.Configuration;
using ServiceDelivery.Simulator.Models;
using ServiceDelivery.Simulator.Services;
using Xunit;

namespace ServiceDelivery.Simulator.Tests.Services;

// SIM-005: the offer-triggered decision engine. AC-1 (human-control gate), AC-2
// (AutoDeclineRatePercent threshold + 1-5s delay), AC-3 (accept/decline as the rep's
// identity), AC-4 (accept-call obligation only), AC-5 (decline produces no extra
// side effect).
public class JobOfferDecisionEngineTests
{
    private const string RepId = "rep-1";

    private static JobOfferPayload Offer(string offerId = "offer-1") =>
        new(offerId, "req-1", "Acme", "Gold", "DTC 1", 41.6, -93.7, 5.0, 10);

    private static RepIdentity Identity(string repId = RepId) =>
        new() { Email = "rep1@dealer.com", Password = "pw", Role = IdentityRole.ServiceRep, RepId = repId };

    private static FleetStateRow Row(string repId = RepId, bool humanControlled = false) =>
        new("V-001", repId, RepState.Available, humanControlled, null);

    private sealed class Harness
    {
        public Mock<IBackendApiClient> Api { get; } = new();
        public Mock<IRepOperationGate> Gate { get; } = new();
        public Mock<IFleetStateView> FleetView { get; } = new();
        public Mock<IDecisionRandomSource> Random { get; } = new();
        public Mock<IResponseDelay> Delay { get; } = new();
        public Mock<IIdentitySessionStore> Store { get; } = new();
        public Mock<ILiveOfferGate> LiveOfferGate { get; } = new();
        public SimulatorOptions Options { get; } = new() { AutoDeclineRatePercent = 15 };

        public Harness()
        {
            // Default: rep is known, operable, accepts (percent at/above rate).
            Gate.Setup(g => g.ShouldOperate(It.IsAny<FleetStateRow>())).Returns(true);
            FleetStateRow? row = Row();
            FleetView.Setup(v => v.TryGetByRepId(RepId, out row)).Returns(true);
            Random.Setup(r => r.NextPercent()).Returns(99);
            Random.Setup(r => r.NextDelaySeconds(It.IsAny<int>(), It.IsAny<int>())).Returns(1);
            Delay.Setup(d => d.DelayAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            Store.Setup(s => s.Reps).Returns(new[] { Identity() });
            // Default: the live-offer latch is free — the offer proceeds to a decision.
            LiveOfferGate.Setup(g => g.TryAcquire(It.IsAny<string>())).Returns(true);
        }

        public JobOfferDecisionEngine Build() =>
            new(Api.Object, Gate.Object, FleetView.Object, Random.Object, Delay.Object,
                Store.Object, LiveOfferGate.Object, Microsoft.Extensions.Options.Options.Create(Options),
                NullLogger<JobOfferDecisionEngine>.Instance);
    }

    [Fact]
    public async Task GivenAnOfferForAHumanControlledRep_WhenHandled_ThenNeitherAcceptNorDeclineIsCalled()
    {
        // Arrange
        var harness = new Harness();
        FleetStateRow? humanRow = Row(humanControlled: true);
        harness.FleetView.Setup(v => v.TryGetByRepId(RepId, out humanRow)).Returns(true);
        harness.Gate.Setup(g => g.ShouldOperate(humanRow!)).Returns(false);
        var engine = harness.Build();

        // Act
        await engine.HandleOfferAsync(RepId, Offer(), CancellationToken.None);

        // Assert
        harness.Api.Verify(a => a.AcceptJobOfferAsync(It.IsAny<string>(), It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.Api.Verify(a => a.DeclineJobOfferAsync(It.IsAny<string>(), It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GivenAnOfferForANonHumanControlledRep_WhenHandled_ThenAResponseApiCallIsMade()
    {
        // Arrange
        var harness = new Harness();
        var engine = harness.Build();

        // Act
        await engine.HandleOfferAsync(RepId, Offer(), CancellationToken.None);

        // Assert
        var acceptCount = harness.Api.Invocations.Count(i => i.Method.Name == nameof(IBackendApiClient.AcceptJobOfferAsync));
        var declineCount = harness.Api.Invocations.Count(i => i.Method.Name == nameof(IBackendApiClient.DeclineJobOfferAsync));
        Assert.Equal(1, acceptCount + declineCount);
    }

    [Fact]
    public async Task GivenRandomBelowDeclineRate_WhenHandled_ThenOfferIsDeclined()
    {
        // Arrange
        var harness = new Harness();
        harness.Random.Setup(r => r.NextPercent()).Returns(14);
        var engine = harness.Build();

        // Act
        await engine.HandleOfferAsync(RepId, Offer(), CancellationToken.None);

        // Assert
        harness.Api.Verify(a => a.DeclineJobOfferAsync("offer-1", It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()), Times.Once);
        harness.Api.Verify(a => a.AcceptJobOfferAsync(It.IsAny<string>(), It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GivenRandomAtOrAboveDeclineRate_WhenHandled_ThenOfferIsAccepted()
    {
        // Arrange
        var harness = new Harness();
        harness.Random.Setup(r => r.NextPercent()).Returns(15);
        var engine = harness.Build();

        // Act
        await engine.HandleOfferAsync(RepId, Offer(), CancellationToken.None);

        // Assert
        harness.Api.Verify(a => a.AcceptJobOfferAsync("offer-1", It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()), Times.Once);
        harness.Api.Verify(a => a.DeclineJobOfferAsync(It.IsAny<string>(), It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(0, 0, false)]    // rate 0 => never declines, even at percent 0
    [InlineData(0, 99, false)]   // rate 0 => never declines at the top of the range
    [InlineData(100, 99, true)]  // rate 100 => always declines, even at the top
    [InlineData(15, 14, true)]   // just below the 15 threshold => decline
    [InlineData(15, 15, false)]  // exactly at the threshold => accept
    public async Task GivenDeclineRate_WhenRandomCrossesThreshold_ThenDecisionHonoursTheKnob(
        int declineRate, int randomPercent, bool expectDecline)
    {
        // Arrange
        var harness = new Harness();
        var engine = new JobOfferDecisionEngine(
            harness.Api.Object, harness.Gate.Object, harness.FleetView.Object,
            harness.Random.Object, harness.Delay.Object, harness.Store.Object,
            harness.LiveOfferGate.Object,
            Microsoft.Extensions.Options.Options.Create(new SimulatorOptions { AutoDeclineRatePercent = declineRate }),
            NullLogger<JobOfferDecisionEngine>.Instance);
        harness.Random.Setup(r => r.NextPercent()).Returns(randomPercent);

        // Act
        await engine.HandleOfferAsync(RepId, Offer(), CancellationToken.None);

        // Assert
        if (expectDecline)
            harness.Api.Verify(a => a.DeclineJobOfferAsync("offer-1", It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()), Times.Once);
        else
            harness.Api.Verify(a => a.AcceptJobOfferAsync("offer-1", It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GivenAnOperableOffer_WhenHandled_ThenA1To5SecondDelayPrecedesTheApiCall()
    {
        // Arrange
        var harness = new Harness();
        harness.Random.Setup(r => r.NextDelaySeconds(1, 5)).Returns(3);
        var callOrder = new List<string>();
        harness.Delay.Setup(d => d.DelayAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("Delay"))
            .Returns(Task.CompletedTask);
        harness.Api.Setup(a => a.AcceptJobOfferAsync(It.IsAny<string>(), It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("Accept"))
            .Returns(Task.FromResult(AcceptOutcome.Accepted));

        // Act
        await harness.Build().HandleOfferAsync(RepId, Offer(), CancellationToken.None);

        // Assert — delay sourced from NextDelaySeconds(1,5), is in [1s,5s], and runs first
        harness.Random.Verify(r => r.NextDelaySeconds(1, 5), Times.Once);
        harness.Delay.Verify(d => d.DelayAsync(TimeSpan.FromSeconds(3), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(new[] { "Delay", "Accept" }, callOrder);
    }

    [Fact]
    public async Task GivenAnAcceptDecision_WhenHandled_ThenAcceptJobOfferAsyncCalledWithOfferIdAndThatRepsIdentity()
    {
        // Arrange — two operated reps; the offer is for rep-1
        var harness = new Harness();
        var rep1 = Identity("rep-1");
        var rep2 = Identity("rep-2");
        harness.Store.Setup(s => s.Reps).Returns(new[] { rep2, rep1 });
        harness.Random.Setup(r => r.NextPercent()).Returns(99); // accept
        var engine = harness.Build();

        // Act
        await engine.HandleOfferAsync("rep-1", Offer("offer-42"), CancellationToken.None);

        // Assert
        harness.Api.Verify(a => a.AcceptJobOfferAsync("offer-42", rep1, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GivenADeclineDecision_WhenHandled_ThenDeclineJobOfferAsyncCalledWithOfferIdAndThatRepsIdentity()
    {
        // Arrange
        var harness = new Harness();
        var rep1 = Identity("rep-1");
        var rep2 = Identity("rep-2");
        harness.Store.Setup(s => s.Reps).Returns(new[] { rep2, rep1 });
        harness.Random.Setup(r => r.NextPercent()).Returns(0); // decline (rate 15)
        var engine = harness.Build();

        // Act
        await engine.HandleOfferAsync("rep-1", Offer("offer-77"), CancellationToken.None);

        // Assert
        harness.Api.Verify(a => a.DeclineJobOfferAsync("offer-77", rep1, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GivenAnAcceptDecision_WhenHandled_ThenOnlyTheAcceptCallIsMadeAndNoNavigationIsTriggeredHere()
    {
        // Arrange — SIM-005's accept obligation is the accept API call ONLY; navigation
        // is driven by the next fleet-state read in SIM-006, not by this engine.
        var harness = new Harness();
        harness.Random.Setup(r => r.NextPercent()).Returns(99); // accept
        var engine = harness.Build();

        // Act
        await engine.HandleOfferAsync(RepId, Offer(), CancellationToken.None);

        // Assert — exactly one accept; no decline; engine produced no other backend side effect
        harness.Api.Verify(a => a.AcceptJobOfferAsync("offer-1", It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()), Times.Once);
        harness.Api.Verify(a => a.DeclineJobOfferAsync(It.IsAny<string>(), It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.Api.Verify(a => a.ClaimVehicleAsync(It.IsAny<string>(), It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.Api.Verify(a => a.PostPositionAsync(It.IsAny<VehiclePosition>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Single(harness.Api.Invocations.Where(i =>
            i.Method.Name is nameof(IBackendApiClient.AcceptJobOfferAsync) or nameof(IBackendApiClient.DeclineJobOfferAsync)));
    }

    // ─── QUAL-029 AC-1: a rep holds at most one live offer at a time ──────────────────

    [Fact]
    public async Task GivenARepWithLiveOfferInProgress_WhenSecondOfferArrives_ThenItIsDeclinedSoTheRequestReMatchesImmediately()
    {
        // Arrange — the latch is already held for this rep (an offer is in flight).
        var harness = new Harness();
        harness.LiveOfferGate.Setup(g => g.TryAcquire(RepId)).Returns(false);
        var engine = harness.Build();

        // Act
        await engine.HandleOfferAsync(RepId, Offer("offer-2"), CancellationToken.None);

        // Assert — BUG-062: the concurrent offer must be RELINQUISHED via decline, not
        // silently ignored. A rep with an undecided offer is still Available on the backend,
        // so an ignored second offer sits Pending until ~60s expiry while the backend's
        // idempotency guard blocks the request from re-matching to a free rep (the BUG-061
        // stuck-Pending class). Declining re-matches the request immediately. It is never
        // accepted (the rep is already committed to its live offer).
        harness.Api.Verify(a => a.DeclineJobOfferAsync("offer-2", It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()), Times.Once);
        harness.Api.Verify(a => a.AcceptJobOfferAsync(It.IsAny<string>(), It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GivenARepWithLiveOfferInProgressAndNoOperatedIdentity_WhenSecondOfferArrives_ThenNoApiCallIsMade()
    {
        // Arrange — latch held, but the rep has no operated identity to decline under.
        var harness = new Harness();
        harness.LiveOfferGate.Setup(g => g.TryAcquire(RepId)).Returns(false);
        harness.Store.Setup(s => s.Reps).Returns(Array.Empty<RepIdentity>());
        var engine = harness.Build();

        // Act
        await engine.HandleOfferAsync(RepId, Offer("offer-2"), CancellationToken.None);

        // Assert — cannot decline without an identity; degrade to a no-op rather than throw.
        harness.Api.Verify(a => a.DeclineJobOfferAsync(It.IsAny<string>(), It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.Api.Verify(a => a.AcceptJobOfferAsync(It.IsAny<string>(), It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GivenARepWithNoActiveOffer_WhenOfferHandled_ThenGateIsReleasedAfterDecision()
    {
        // Arrange
        var harness = new Harness();
        var engine = harness.Build();

        // Act
        await engine.HandleOfferAsync(RepId, Offer(), CancellationToken.None);

        // Assert — the latch is freed so the rep can receive a later offer.
        harness.LiveOfferGate.Verify(g => g.Release(RepId), Times.Once);
    }

    [Fact]
    public async Task GivenARepWithNoIdentityFound_WhenOfferHandled_ThenGateIsReleasedAfterEarlyReturn()
    {
        // Arrange — the latch is acquired but no operated identity matches, forcing the
        // early return; the finally must still free the latch.
        var harness = new Harness();
        harness.Store.Setup(s => s.Reps).Returns(Array.Empty<RepIdentity>());
        var engine = harness.Build();

        // Act
        await engine.HandleOfferAsync(RepId, Offer(), CancellationToken.None);

        // Assert
        harness.LiveOfferGate.Verify(g => g.Release(RepId), Times.Once);
    }

    // ─── QUAL-029 AC-2: a 409 on accept triggers an immediate decline ─────────────────

    [Fact]
    public async Task GivenAnAcceptRejectedWith409_WhenHandled_ThenDeclineJobOfferAsyncIsCalled()
    {
        // Arrange — the rep decides to accept, but the backend rejects it with a 409.
        var harness = new Harness();
        harness.Random.Setup(r => r.NextPercent()).Returns(99); // accept
        harness.Api.Setup(a => a.AcceptJobOfferAsync("offer-1", It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AcceptOutcome.Conflict);
        var engine = harness.Build();

        // Act
        await engine.HandleOfferAsync(RepId, Offer(), CancellationToken.None);

        // Assert
        harness.Api.Verify(a => a.DeclineJobOfferAsync("offer-1", It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GivenAnAcceptReturningAccepted_WhenHandled_ThenDeclineIsNotCalled()
    {
        // Arrange — the accept succeeds; no compensating decline should follow.
        var harness = new Harness();
        harness.Random.Setup(r => r.NextPercent()).Returns(99); // accept
        harness.Api.Setup(a => a.AcceptJobOfferAsync("offer-1", It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AcceptOutcome.Accepted);
        var engine = harness.Build();

        // Act
        await engine.HandleOfferAsync(RepId, Offer(), CancellationToken.None);

        // Assert
        harness.Api.Verify(a => a.DeclineJobOfferAsync(It.IsAny<string>(), It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GivenADeclineDecision_WhenHandled_ThenNoJobStateOrNavigationSideEffectIsProduced()
    {
        // Arrange — on decline (or expiry) the vehicle keeps its loop; the engine must
        // produce no accept and no fleet-state mutation. Loop continuation is owned by
        // the position engine, not here.
        var harness = new Harness();
        harness.Random.Setup(r => r.NextPercent()).Returns(0); // decline
        var engine = harness.Build();

        // Act
        await engine.HandleOfferAsync(RepId, Offer(), CancellationToken.None);

        // Assert
        harness.Api.Verify(a => a.DeclineJobOfferAsync("offer-1", It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()), Times.Once);
        harness.Api.Verify(a => a.AcceptJobOfferAsync(It.IsAny<string>(), It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.Api.Verify(a => a.ClaimVehicleAsync(It.IsAny<string>(), It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.Api.Verify(a => a.PostPositionAsync(It.IsAny<VehiclePosition>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
