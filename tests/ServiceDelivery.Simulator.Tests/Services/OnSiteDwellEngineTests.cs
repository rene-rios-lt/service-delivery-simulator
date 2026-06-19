using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceDelivery.Simulator.Models;
using ServiceDelivery.Simulator.Services;
using Xunit;

namespace ServiceDelivery.Simulator.Tests.Services;

// SIM-010 — drives the on-site dwell→complete progression for operated reps.
// All elapsed checks run against a mutable FakeClock (no real waiting); the dwell
// duration is forced via a Moq IDecisionRandomSource so completion timing is exact.
public class OnSiteDwellEngineTests
{
    private const string VehicleId = "vehicle-1";
    private const string RepId = "rep-1";

    private static RepIdentity Identity(string repId = RepId) =>
        new RepIdentity
        {
            Email = "rep1@dealer.com",
            Password = "pw",
            Role = IdentityRole.ServiceRep,
            RepId = repId,
            Token = "rep1-token"
        };

    private static FleetStateRow OnSiteRow(
        string vehicleId = VehicleId,
        string? repId = RepId,
        bool humanControlled = false,
        RepState state = RepState.OnSite) =>
        new FleetStateRow(
            vehicleId,
            repId,
            state,
            humanControlled,
            new RequesterLocation(41.5, -93.6));

    private static Mock<IIdentitySessionStore> StoreWith(params RepIdentity[] reps)
    {
        var store = new Mock<IIdentitySessionStore>();
        store.Setup(s => s.Reps).Returns(reps);
        return store;
    }

    private static Mock<IDecisionRandomSource> RandomReturning(int dwellSeconds)
    {
        var rng = new Mock<IDecisionRandomSource>();
        rng.Setup(r => r.NextDelaySeconds(It.IsAny<int>(), It.IsAny<int>()))
            .Returns(dwellSeconds);
        return rng;
    }

    private static OnSiteDwellEngine BuildEngine(
        Mock<IBackendApiClient> apiClient,
        ISystemClock clock,
        Mock<IDecisionRandomSource>? rng = null,
        Mock<IIdentitySessionStore>? store = null) =>
        new OnSiteDwellEngine(
            apiClient.Object,
            (rng ?? RandomReturning(120)).Object,
            clock,
            (store ?? StoreWith(Identity())).Object,
            NullLogger<OnSiteDwellEngine>.Instance);

    // ─── AC-1: dwell then complete ───────────────────────────────────────────

    [Fact]
    public async Task GivenAnOnSiteAutomatedRep_WhenDwellHasElapsedAcrossTicks_ThenCompleteAsyncCalledOnce()
    {
        // Arrange — fixed 120s dwell; first tick records it, second tick is past it
        var apiClient = new Mock<IBackendApiClient>();
        var clock = new FakeClock();
        var rep = Identity();
        var engine = BuildEngine(apiClient, clock, RandomReturning(120), StoreWith(rep));
        var row = OnSiteRow();

        // Act
        await engine.RunAsync(row, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(121));
        await engine.RunAsync(row, CancellationToken.None);

        // Assert
        apiClient.Verify(
            c => c.CompleteAsync(rep, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GivenAnOnSiteAutomatedRep_WhenDwellHasNotYetElapsed_ThenCompleteAsyncNotCalled()
    {
        // Arrange — 120s dwell; only 60s passes between ticks
        var apiClient = new Mock<IBackendApiClient>();
        var clock = new FakeClock();
        var engine = BuildEngine(apiClient, clock, RandomReturning(120));
        var row = OnSiteRow();

        // Act
        await engine.RunAsync(row, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(60));
        await engine.RunAsync(row, CancellationToken.None);

        // Assert
        apiClient.Verify(
            c => c.CompleteAsync(It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GivenAnOnSiteRepAlreadyCompleted_WhenRunAgain_ThenCompleteAsyncNotCalledTwice()
    {
        // Arrange — 120s dwell elapses, then engine ticks twice more past it
        var apiClient = new Mock<IBackendApiClient>();
        var clock = new FakeClock();
        var rep = Identity();
        var engine = BuildEngine(apiClient, clock, RandomReturning(120), StoreWith(rep));
        var row = OnSiteRow();

        // Act
        await engine.RunAsync(row, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(121));
        await engine.RunAsync(row, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(10));
        await engine.RunAsync(row, CancellationToken.None);

        // Assert
        apiClient.Verify(
            c => c.CompleteAsync(rep, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GivenAnEnRouteAutomatedRep_WhenRun_ThenNoDwellStartedAndCompleteNotCalled()
    {
        // Arrange — rep is EnRoute, not yet OnSite; arrive is owned by SIM-006
        var apiClient = new Mock<IBackendApiClient>();
        var clock = new FakeClock();
        var rng = RandomReturning(120);
        var engine = BuildEngine(apiClient, clock, rng);
        var row = OnSiteRow(state: RepState.EnRoute);

        // Act
        await engine.RunAsync(row, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(300));
        await engine.RunAsync(row, CancellationToken.None);

        // Assert
        rng.Verify(r => r.NextDelaySeconds(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        apiClient.Verify(
            c => c.CompleteAsync(It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ─── AC-2: dwell randomized per job so completions stagger ───────────────

    [Fact]
    public async Task GivenAnOnSiteRep_WhenDwellStarts_ThenDurationIsDrawnFromRandomSource()
    {
        // Arrange — RNG returns 150s; the dwell must be that long, not a hardcoded value
        var apiClient = new Mock<IBackendApiClient>();
        var clock = new FakeClock();
        var rep = Identity();
        var engine = BuildEngine(apiClient, clock, RandomReturning(150), StoreWith(rep));
        var row = OnSiteRow();

        // Act — first tick draws the dwell; at 149s it must NOT complete, at 151s it must
        await engine.RunAsync(row, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(149));
        await engine.RunAsync(row, CancellationToken.None);
        var beforeThreshold = apiClient.Invocations.Count;
        clock.Advance(TimeSpan.FromSeconds(2));
        await engine.RunAsync(row, CancellationToken.None);

        // Assert
        Assert.Equal(0, beforeThreshold);
        apiClient.Verify(
            c => c.CompleteAsync(rep, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GivenAVehicleCompletedThenOnSiteForANewJob_WhenRun_ThenANewDwellDurationIsDrawn()
    {
        // Arrange
        var apiClient = new Mock<IBackendApiClient>();
        var clock = new FakeClock();
        var rep = Identity();
        var rng = RandomReturning(120);
        var engine = BuildEngine(apiClient, clock, rng, StoreWith(rep));
        var row = OnSiteRow();

        // Act — first job: dwell + complete; rep returns Available; then a new OnSite job
        await engine.RunAsync(row, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(121));
        await engine.RunAsync(row, CancellationToken.None);
        await engine.RunAsync(OnSiteRow(state: RepState.Available), CancellationToken.None);
        await engine.RunAsync(row, CancellationToken.None);

        // Assert — a fresh draw happened for the second job (two draws total)
        rng.Verify(r => r.NextDelaySeconds(120, 240), Times.Exactly(2));
    }

    [Fact]
    public async Task GivenTwoRepsWithDifferentDrawnDwells_WhenTicked_ThenEachCompletesAtItsOwnElapsedTime()
    {
        // Arrange — vehicle A draws 120s, vehicle B draws 200s
        var apiClient = new Mock<IBackendApiClient>();
        var clock = new FakeClock();
        var repA = Identity("rep-A");
        var repB = Identity("rep-B");
        var rng = new Mock<IDecisionRandomSource>();
        rng.SetupSequence(r => r.NextDelaySeconds(120, 240))
            .Returns(120)
            .Returns(200);
        var engine = BuildEngine(apiClient, clock, rng, StoreWith(repA, repB));
        var rowA = OnSiteRow(vehicleId: "vehicle-A", repId: "rep-A");
        var rowB = OnSiteRow(vehicleId: "vehicle-B", repId: "rep-B");

        // Act — both start at T0; advance to 130s (A done, B not), then to 210s (B done)
        await engine.RunAsync(rowA, CancellationToken.None);
        await engine.RunAsync(rowB, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(130));
        await engine.RunAsync(rowA, CancellationToken.None);
        await engine.RunAsync(rowB, CancellationToken.None);
        var bDoneEarly = apiClient.Invocations
            .Count(i => i.Method.Name == nameof(IBackendApiClient.CompleteAsync)
                && ReferenceEquals(i.Arguments[0], repB));
        clock.Advance(TimeSpan.FromSeconds(80));
        await engine.RunAsync(rowB, CancellationToken.None);

        // Assert
        Assert.Equal(0, bDoneEarly);
        apiClient.Verify(c => c.CompleteAsync(repA, It.IsAny<CancellationToken>()), Times.Once);
        apiClient.Verify(c => c.CompleteAsync(repB, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── AC-3: 120–240 window is a code constant, no config knob ─────────────

    [Fact]
    public async Task GivenAnOnSiteRep_WhenDwellStarts_ThenRandomSourceIsAskedForRangeOneHundredTwentyToTwoHundredForty()
    {
        // Arrange
        var apiClient = new Mock<IBackendApiClient>();
        var clock = new FakeClock();
        var rng = RandomReturning(120);
        var engine = BuildEngine(apiClient, clock, rng);
        var row = OnSiteRow();

        // Act
        await engine.RunAsync(row, CancellationToken.None);

        // Assert — proves the constant range, not a configurable value
        rng.Verify(r => r.NextDelaySeconds(120, 240), Times.Once);
    }

    [Fact]
    public void GivenTheOnSiteDwellEngine_WhenConstructed_ThenItDoesNotDependOnSimulatorOptions()
    {
        // Arrange / Act — the public constructor is the contract; assert no parameter
        // is an IOptions<SimulatorOptions> (or SimulatorOptions). The dwell window is a
        // code constant, so the engine must not read any config.
        var constructorParameterTypes = typeof(OnSiteDwellEngine)
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .ToList();

        // Assert
        Assert.DoesNotContain(
            constructorParameterTypes,
            t => t.FullName!.Contains("SimulatorOptions"));
    }

    // ─── AC-4: automated reps only; human/yielded reps never auto-complete ───

    [Fact]
    public async Task GivenAHumanControlledRep_WhenRun_ThenCompleteAsyncNeverCalled()
    {
        // Arrange — an OnSite row whose rep is human-controlled
        var apiClient = new Mock<IBackendApiClient>();
        var clock = new FakeClock();
        var rep = Identity();
        var rng = RandomReturning(120);
        var engine = BuildEngine(apiClient, clock, rng, StoreWith(rep));
        var row = OnSiteRow(humanControlled: true);

        // Act
        await engine.RunAsync(row, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(300));
        await engine.RunAsync(row, CancellationToken.None);

        // Assert — no dwell drawn, no completion
        rng.Verify(r => r.NextDelaySeconds(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        apiClient.Verify(
            c => c.CompleteAsync(It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GivenAnOnSiteRepMidDwell_WhenRepBecomesHumanControlledBeforeElapsed_ThenCompleteAsyncNeverCalled()
    {
        // Arrange — dwell starts automated, then a human takes over before it elapses
        var apiClient = new Mock<IBackendApiClient>();
        var clock = new FakeClock();
        var rep = Identity();
        var engine = BuildEngine(apiClient, clock, RandomReturning(120), StoreWith(rep));
        var automatedRow = OnSiteRow(humanControlled: false);
        var takenOverRow = OnSiteRow(humanControlled: true);

        // Act
        await engine.RunAsync(automatedRow, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(200));
        await engine.RunAsync(takenOverRow, CancellationToken.None);

        // Assert — the mid-dwell takeover must never auto-complete
        apiClient.Verify(
            c => c.CompleteAsync(It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GivenAYieldedRep_WhenRun_ThenCompleteAsyncNeverCalled()
    {
        // Arrange — a SIM-009-yielded rep surfaces as a human-controlled row (the
        // reconciler gate never operates it; the engine's guard proves it directly)
        var apiClient = new Mock<IBackendApiClient>();
        var clock = new FakeClock();
        var rep = Identity();
        var rng = RandomReturning(120);
        var engine = BuildEngine(apiClient, clock, rng, StoreWith(rep));
        var yieldedRow = OnSiteRow(humanControlled: true);

        // Act
        await engine.RunAsync(yieldedRow, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(300));
        await engine.RunAsync(yieldedRow, CancellationToken.None);

        // Assert
        rng.Verify(r => r.NextDelaySeconds(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        apiClient.Verify(
            c => c.CompleteAsync(It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
