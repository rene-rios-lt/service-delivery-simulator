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

    private static Mock<IBackendApiClient> DefaultApiClientMock(string jwt = "test-jwt")
    {
        var mock = new Mock<IBackendApiClient>();
        mock.Setup(c => c.AuthenticateAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(c => c.StoredJwt).Returns(jwt);
        return mock;
    }

    private static Mock<ISignalRClient> DefaultSignalRClientMock()
    {
        var mock = new Mock<ISignalRClient>();
        mock.Setup(c => c.ConnectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return mock;
    }

    private static ILogger<SimulatorStartupService> NullLogger() =>
        new Mock<ILogger<SimulatorStartupService>>().Object;

    // ─── AC-2: RegisterJobOfferHandler called before ConnectAsync ────────────

    [Fact]
    public async Task GivenStartupService_WhenExecuteAsyncRuns_ThenRegisterJobOfferHandlerCalledBeforeConnectAsync()
    {
        // Arrange
        var callOrder = new List<string>();

        var apiClientMock = DefaultApiClientMock();

        var signalRClientMock = new Mock<ISignalRClient>();
        signalRClientMock
            .Setup(c => c.RegisterJobOfferHandler(It.IsAny<Action<JobOfferPayload>>()))
            .Callback(() => callOrder.Add("RegisterJobOfferHandler"));
        signalRClientMock
            .Setup(c => c.ConnectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("ConnectAsync"))
            .Returns(Task.CompletedTask);

        var service = new SimulatorStartupService(apiClientMock.Object, signalRClientMock.Object, NullLogger());

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert — handler registration must precede connect
        var registerIndex = callOrder.IndexOf("RegisterJobOfferHandler");
        var connectIndex = callOrder.IndexOf("ConnectAsync");
        Assert.True(registerIndex >= 0, "RegisterJobOfferHandler was not called");
        Assert.True(connectIndex >= 0, "ConnectAsync was not called");
        Assert.True(registerIndex < connectIndex,
            $"Expected RegisterJobOfferHandler (index {registerIndex}) before ConnectAsync (index {connectIndex})");
    }

    [Fact]
    public async Task GivenStartupService_WhenExecuteAsyncRuns_ThenAuthenticateCalledBeforeConnectAsync()
    {
        // Arrange
        var callOrder = new List<string>();

        var apiClientMock = new Mock<IBackendApiClient>();
        apiClientMock
            .Setup(c => c.AuthenticateAsync(It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("AuthenticateAsync"))
            .Returns(Task.CompletedTask);
        apiClientMock.Setup(c => c.StoredJwt).Returns("test-jwt");

        var signalRClientMock = new Mock<ISignalRClient>();
        signalRClientMock
            .Setup(c => c.RegisterJobOfferHandler(It.IsAny<Action<JobOfferPayload>>()))
            .Callback(() => callOrder.Add("RegisterJobOfferHandler"));
        signalRClientMock
            .Setup(c => c.ConnectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("ConnectAsync"))
            .Returns(Task.CompletedTask);

        var service = new SimulatorStartupService(apiClientMock.Object, signalRClientMock.Object, NullLogger());

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert — authenticate must precede connect
        var authIndex = callOrder.IndexOf("AuthenticateAsync");
        var connectIndex = callOrder.IndexOf("ConnectAsync");
        Assert.True(authIndex >= 0, "AuthenticateAsync was not called");
        Assert.True(connectIndex >= 0, "ConnectAsync was not called");
        Assert.True(authIndex < connectIndex,
            $"Expected AuthenticateAsync (index {authIndex}) before ConnectAsync (index {connectIndex})");
    }

    [Fact]
    public async Task GivenStartupService_WhenExecuteAsyncRuns_ThenConnectAsyncReceivesStoredJwt()
    {
        // Arrange
        const string expectedJwt = "stored-jwt-from-auth";
        string? capturedJwt = null;

        var apiClientMock = DefaultApiClientMock(jwt: expectedJwt);

        var signalRClientMock = new Mock<ISignalRClient>();
        signalRClientMock
            .Setup(c => c.ConnectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((jwt, _) => capturedJwt = jwt)
            .Returns(Task.CompletedTask);

        var service = new SimulatorStartupService(apiClientMock.Object, signalRClientMock.Object, NullLogger());

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert
        Assert.Equal(expectedJwt, capturedJwt);
    }

    // ─── AC-4: Workers not blocked when SignalR connection fails ─────────────

    [Fact]
    public async Task GivenSignalRConnectionFails_WhenStartupServiceRuns_ThenStartAsyncCompletesWithoutException()
    {
        // Arrange
        var apiClientMock = DefaultApiClientMock();

        var signalRClientMock = new Mock<ISignalRClient>();
        signalRClientMock
            .Setup(c => c.ConnectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Connection failed"));

        var service = new SimulatorStartupService(apiClientMock.Object, signalRClientMock.Object, NullLogger());

        // Act
        var exception = await Record.ExceptionAsync(
            () => service.StartAsync(CancellationToken.None));

        // Assert — startup completes without exception; VehicleWorkers are not blocked
        Assert.Null(exception);
    }
}
