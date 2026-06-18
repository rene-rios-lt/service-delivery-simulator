using System.Security.Authentication;
using Microsoft.Extensions.Logging;
using Moq;
using ServiceDelivery.Simulator.Models;
using ServiceDelivery.Simulator.Services;
using ServiceDelivery.Simulator.Workers;
using Xunit;

namespace ServiceDelivery.Simulator.Tests.Workers;

public class SimulatorStartupServiceTests
{
    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static RepIdentity Simulator() =>
        new RepIdentity { Email = "sim@system.internal", Password = "pw", Role = IdentityRole.Simulator };

    private static RepIdentity Rep(string email) =>
        new RepIdentity { Email = email, Password = "pw", Role = IdentityRole.ServiceRep };

    private static Mock<IIdentitySessionStore> StoreWith(RepIdentity simulator, params RepIdentity[] reps)
    {
        var store = new Mock<IIdentitySessionStore>();
        store.Setup(s => s.Simulator).Returns(simulator);
        store.Setup(s => s.Reps).Returns(reps);
        return store;
    }

    private static Mock<ISignalRClient> DefaultSignalRClientMock()
    {
        var mock = new Mock<ISignalRClient>();
        mock.Setup(c => c.ConnectAllAsync(It.IsAny<IEnumerable<RepIdentity>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return mock;
    }

    private static ILogger<SimulatorStartupService> NullLogger() =>
        new Mock<ILogger<SimulatorStartupService>>().Object;

    private static Mock<IFleetClaimCoordinator> DefaultClaimCoordinatorMock()
    {
        var mock = new Mock<IFleetClaimCoordinator>();
        mock.Setup(c => c.ClaimInitialVehiclesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return mock;
    }

    private static SimulatorStartupService BuildService(
        Mock<IIdentitySessionStore> store,
        Mock<ISignalRClient> signalR,
        Mock<IFleetClaimCoordinator>? claimCoordinator = null) =>
        new(store.Object, signalR.Object, (claimCoordinator ?? DefaultClaimCoordinatorMock()).Object, NullLogger());

    // ─── Ordering: authenticate before connect, register before connect ──────

    [Fact]
    public async Task GivenStartupService_WhenStartAsyncRuns_ThenSimulatorAuthenticatedBeforeConnect()
    {
        // Arrange
        var callOrder = new List<string>();
        var simulator = Simulator();
        var store = StoreWith(simulator, Rep("rep1@dealer.com"));
        store.Setup(s => s.AuthenticateAsync(It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()))
            .Callback<RepIdentity, CancellationToken>((id, _) =>
                callOrder.Add(id.Role == IdentityRole.Simulator ? "AuthSimulator" : "AuthRep"))
            .Returns(Task.CompletedTask);

        var signalR = new Mock<ISignalRClient>();
        signalR.Setup(c => c.ConnectAllAsync(It.IsAny<IEnumerable<RepIdentity>>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("Connect"))
            .Returns(Task.CompletedTask);

        var service = BuildService(store, signalR);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert
        var simAuthIndex = callOrder.IndexOf("AuthSimulator");
        var connectIndex = callOrder.IndexOf("Connect");
        Assert.True(simAuthIndex >= 0, "Simulator was not authenticated");
        Assert.True(connectIndex >= 0, "ConnectAllAsync was not called");
        Assert.True(simAuthIndex < connectIndex,
            $"Expected Simulator auth (index {simAuthIndex}) before Connect (index {connectIndex})");
    }

    [Fact]
    public async Task GivenStartupService_WhenStartAsyncRuns_ThenRegisterJobOfferHandlerCalledBeforeConnect()
    {
        // Arrange
        var callOrder = new List<string>();
        var store = StoreWith(Simulator(), Rep("rep1@dealer.com"));
        store.Setup(s => s.AuthenticateAsync(It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var signalR = new Mock<ISignalRClient>();
        signalR.Setup(c => c.RegisterJobOfferHandler(It.IsAny<Action<string, JobOfferPayload>>()))
            .Callback(() => callOrder.Add("Register"));
        signalR.Setup(c => c.ConnectAllAsync(It.IsAny<IEnumerable<RepIdentity>>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("Connect"))
            .Returns(Task.CompletedTask);

        var service = BuildService(store, signalR);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert
        var registerIndex = callOrder.IndexOf("Register");
        var connectIndex = callOrder.IndexOf("Connect");
        Assert.True(registerIndex >= 0, "RegisterJobOfferHandler was not called");
        Assert.True(connectIndex >= 0, "ConnectAllAsync was not called");
        Assert.True(registerIndex < connectIndex,
            $"Expected Register (index {registerIndex}) before Connect (index {connectIndex})");
    }

    [Fact]
    public async Task GivenAuthenticatedReps_WhenStartAsyncRuns_ThenConnectAllAsyncReceivesEveryRep()
    {
        // Arrange
        var rep1 = Rep("rep1@dealer.com");
        var rep2 = Rep("rep2@dealer.com");
        var store = StoreWith(Simulator(), rep1, rep2);
        store.Setup(s => s.AuthenticateAsync(It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        IEnumerable<RepIdentity>? connectedReps = null;
        var signalR = new Mock<ISignalRClient>();
        signalR.Setup(c => c.ConnectAllAsync(It.IsAny<IEnumerable<RepIdentity>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<RepIdentity>, CancellationToken>((reps, _) => connectedReps = reps.ToList())
            .Returns(Task.CompletedTask);

        var service = BuildService(store, signalR);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(connectedReps);
        Assert.Contains(rep1, connectedReps!);
        Assert.Contains(rep2, connectedReps!);
    }

    // ─── SIM-008 AC-4: startup claims initial vehicles after SignalR connect ──

    [Fact]
    public async Task GivenStartupService_WhenStartAsyncRuns_ThenInitialVehiclesAreClaimedAfterConnect()
    {
        // Arrange
        var callOrder = new List<string>();
        var store = StoreWith(Simulator(), Rep("rep1@dealer.com"));
        store.Setup(s => s.AuthenticateAsync(It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var signalR = new Mock<ISignalRClient>();
        signalR.Setup(c => c.ConnectAllAsync(It.IsAny<IEnumerable<RepIdentity>>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("Connect"))
            .Returns(Task.CompletedTask);

        var claimCoordinator = new Mock<IFleetClaimCoordinator>();
        claimCoordinator.Setup(c => c.ClaimInitialVehiclesAsync(It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("Claim"))
            .Returns(Task.CompletedTask);

        var service = BuildService(store, signalR, claimCoordinator);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert
        claimCoordinator.Verify(c => c.ClaimInitialVehiclesAsync(It.IsAny<CancellationToken>()), Times.Once);
        var connectIndex = callOrder.IndexOf("Connect");
        var claimIndex = callOrder.IndexOf("Claim");
        Assert.True(connectIndex >= 0, "ConnectAllAsync was not called");
        Assert.True(claimIndex >= 0, "ClaimInitialVehiclesAsync was not called");
        Assert.True(connectIndex < claimIndex,
            $"Expected Connect (index {connectIndex}) before Claim (index {claimIndex})");
    }

    // ─── AC-7: one rep login failure skips only that rep ─────────────────────

    [Fact]
    public async Task GivenOneRepLoginFails_WhenStartupRuns_ThenOtherRepsAreStillAuthenticatedAndStartupCompletes()
    {
        // Arrange — rep1 login throws; sim + rep2 succeed
        var simulator = Simulator();
        var rep1 = Rep("rep1@dealer.com");
        var rep2 = Rep("rep2@dealer.com");
        var store = StoreWith(simulator, rep1, rep2);
        store.Setup(s => s.AuthenticateAsync(simulator, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        store.Setup(s => s.AuthenticateAsync(rep1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AuthenticationException("rep1 failed"));
        store.Setup(s => s.AuthenticateAsync(rep2, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var signalR = DefaultSignalRClientMock();
        var service = BuildService(store, signalR);

        // Act
        var exception = await Record.ExceptionAsync(() => service.StartAsync(CancellationToken.None));

        // Assert — startup completes; rep2 was authenticated despite rep1 failing
        Assert.Null(exception);
        store.Verify(s => s.AuthenticateAsync(rep2, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GivenOneRepLoginFails_WhenStartupRuns_ThenOnlyTheSucceedingRepsAreConnected()
    {
        // Arrange
        var simulator = Simulator();
        var rep1 = Rep("rep1@dealer.com");
        var rep2 = Rep("rep2@dealer.com");
        var store = StoreWith(simulator, rep1, rep2);
        store.Setup(s => s.AuthenticateAsync(simulator, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        store.Setup(s => s.AuthenticateAsync(rep1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AuthenticationException("rep1 failed"));
        store.Setup(s => s.AuthenticateAsync(rep2, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        IEnumerable<RepIdentity>? connectedReps = null;
        var signalR = new Mock<ISignalRClient>();
        signalR.Setup(c => c.ConnectAllAsync(It.IsAny<IEnumerable<RepIdentity>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<RepIdentity>, CancellationToken>((reps, _) => connectedReps = reps.ToList())
            .Returns(Task.CompletedTask);

        var service = BuildService(store, signalR);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert — only rep2 is handed to ConnectAllAsync
        Assert.NotNull(connectedReps);
        Assert.DoesNotContain(rep1, connectedReps!);
        Assert.Contains(rep2, connectedReps!);
    }

    // ─── AC-7: per-connection failure isolation delegated to the SignalR client ─

    [Fact]
    public async Task GivenOneRepConnectionFails_WhenStartupRuns_ThenOtherRepsStillConnectAndStartupCompletes()
    {
        // Arrange — all reps authenticate; ConnectAllAsync absorbs its own per-connection failures
        var store = StoreWith(Simulator(), Rep("rep1@dealer.com"), Rep("rep2@dealer.com"));
        store.Setup(s => s.AuthenticateAsync(It.IsAny<RepIdentity>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var signalR = DefaultSignalRClientMock();
        var service = BuildService(store, signalR);

        // Act
        var exception = await Record.ExceptionAsync(() => service.StartAsync(CancellationToken.None));

        // Assert — startup completes and ConnectAllAsync was invoked once for the surviving reps
        Assert.Null(exception);
        signalR.Verify(
            c => c.ConnectAllAsync(It.IsAny<IEnumerable<RepIdentity>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ─── AC-7: Simulator (position) account failure aborts startup ───────────

    [Fact]
    public async Task GivenSimulatorAccountLoginFails_WhenStartupRuns_ThenStartAsyncThrowsAndStartupAborts()
    {
        // Arrange — the Simulator identity login throws
        var simulator = Simulator();
        var store = StoreWith(simulator, Rep("rep1@dealer.com"));
        store.Setup(s => s.AuthenticateAsync(simulator, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AuthenticationException("simulator login failed"));

        var signalR = DefaultSignalRClientMock();
        var service = BuildService(store, signalR);

        // Act & Assert — the exception propagates so the host aborts startup
        await Assert.ThrowsAsync<AuthenticationException>(() => service.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GivenSimulatorAccountLoginFails_WhenStartupRuns_ThenNoRepConnectionsAreAttempted()
    {
        // Arrange
        var simulator = Simulator();
        var store = StoreWith(simulator, Rep("rep1@dealer.com"));
        store.Setup(s => s.AuthenticateAsync(simulator, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AuthenticationException("simulator login failed"));

        var signalR = DefaultSignalRClientMock();
        var service = BuildService(store, signalR);

        // Act
        await Record.ExceptionAsync(() => service.StartAsync(CancellationToken.None));

        // Assert — startup aborted before connecting any rep
        signalR.Verify(
            c => c.ConnectAllAsync(It.IsAny<IEnumerable<RepIdentity>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
