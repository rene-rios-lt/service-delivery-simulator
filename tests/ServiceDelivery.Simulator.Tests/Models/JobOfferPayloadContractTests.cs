using System.Text.Json;
using ServiceDelivery.Simulator.Models;
using Xunit;

namespace ServiceDelivery.Simulator.Tests.Models;

// BUG-060: ADR-0011 captured-real-payload contract test for the JobOfferReceived SignalR
// payload. The backend sends etaMinutes as a double (JobOfferReceivedPayload.EtaMinutes is
// double); the simulator typed it as int, so any fractional ETA (e.g. 12.7) threw a
// JsonException during SignalR On<JobOfferPayload> binding, and the offer never reached
// JobOfferDecisionEngine.HandleOfferAsync. These tests run a captured camelCase wire payload
// through the SAME System.Text.Json path the SignalR JsonHubProtocol uses
// (JsonSerializerDefaults.Web). Distinct per-field values guard against a field-name drift
// coincidentally producing a spurious pass.
public class JobOfferPayloadContractTests
{
    private static JsonSerializerOptions WebOptions() =>
        new(JsonSerializerDefaults.Web);

    [Fact]
    public void GivenACapturedJobOfferReceivedPayloadWithFractionalEta_WhenDeserialized_ThenAllFieldsBindCorrectly()
    {
        // Arrange
        const string json = """
        {
          "offerId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
          "requestId": "b2c3d4e5-f6a7-8901-bcde-f12345678901",
          "requesterName": "Alice Tester",
          "requesterTier": "Gold",
          "dtcTitle": "P0420 Catalyst Efficiency",
          "latitude": 41.5978,
          "longitude": -93.6124,
          "distanceMiles": 8.3,
          "etaMinutes": 12.7
        }
        """;

        // Act
        var result = JsonSerializer.Deserialize<JobOfferPayload>(json, WebOptions());

        // Assert
        Assert.NotNull(result);
        Assert.Equal("a1b2c3d4-e5f6-7890-abcd-ef1234567890", result!.OfferId);
        Assert.Equal("b2c3d4e5-f6a7-8901-bcde-f12345678901", result.RequestId);
        Assert.Equal("Alice Tester", result.RequesterName);
        Assert.Equal("Gold", result.RequesterTier);
        Assert.Equal("P0420 Catalyst Efficiency", result.DtcTitle);
        Assert.Equal(41.5978, result.Latitude);
        Assert.Equal(-93.6124, result.Longitude);
        Assert.Equal(8.3, result.DistanceMiles);
        Assert.Equal(12.7, result.EtaMinutes);
    }

    [Fact]
    public void GivenAJobOfferReceivedPayloadWithWholeNumberEta_WhenDeserialized_ThenBindsWithNoRegression()
    {
        // Arrange
        const string json = """
        {
          "offerId": "c3d4e5f6-a7b8-9012-cdef-123456789012",
          "requestId": "d4e5f6a7-b8c9-0123-defa-234567890123",
          "requesterName": "Bob Driver",
          "requesterTier": "Silver",
          "dtcTitle": "P0300 Random Misfire",
          "latitude": 41.6832,
          "longitude": -93.4956,
          "distanceMiles": 5.1,
          "etaMinutes": 10
        }
        """;

        // Act
        var result = JsonSerializer.Deserialize<JobOfferPayload>(json, WebOptions());

        // Assert
        Assert.NotNull(result);
        Assert.Equal("c3d4e5f6-a7b8-9012-cdef-123456789012", result!.OfferId);
        Assert.Equal("d4e5f6a7-b8c9-0123-defa-234567890123", result.RequestId);
        Assert.Equal("Bob Driver", result.RequesterName);
        Assert.Equal("Silver", result.RequesterTier);
        Assert.Equal("P0300 Random Misfire", result.DtcTitle);
        Assert.Equal(41.6832, result.Latitude);
        Assert.Equal(-93.4956, result.Longitude);
        Assert.Equal(5.1, result.DistanceMiles);
        Assert.Equal(10.0, result.EtaMinutes);
    }
}
