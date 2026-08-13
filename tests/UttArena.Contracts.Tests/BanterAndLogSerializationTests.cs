using System.Text.Json;
using UttArena.Contracts;

namespace UttArena.Contracts.Tests;

public sealed class BanterAndLogSerializationTests
{
    [Fact]
    public void MoveResponseOmitsNullBanter()
    {
        var response = new MoveResponse(
            ProtocolConstants.Version,
            ProtocolConstants.MoveResponseType,
            "request-17",
            new BoardPosition(4, 7));

        var json = ProtocolJson.SerializeLine(response);

        Assert.Equal(
            "{\"protocolVersion\":1,\"type\":\"move_response\",\"requestId\":\"request-17\",\"move\":{\"row\":4,\"column\":7}}",
            json);
    }

    [Fact]
    public void MoveResponseRoundTripsBanter()
    {
        var response = new MoveResponse(
            ProtocolConstants.Version,
            ProtocolConstants.MoveResponseType,
            "request-17",
            new BoardPosition(4, 7),
            "Nice board.");

        var json = ProtocolJson.SerializeLine(response);
        var roundTrip = ProtocolJson.DeserializeLine<MoveResponse>(json);

        Assert.Contains("\"banter\":\"Nice board.\"", json);
        Assert.Equal("Nice board.", roundTrip.Banter);
    }

    [Fact]
    public void NormalizeBanterRejectsNewlinesAndOversize()
    {
        Assert.Null(ProtocolValidation.NormalizeBanter("a\nb"));
        Assert.Null(ProtocolValidation.NormalizeBanter(new string('x', ProtocolLimits.MaxBanterLength + 1)));
        Assert.Equal("hi", ProtocolValidation.NormalizeBanter("  hi  "));
    }

    [Fact]
    public void ProtocolLogRoundTripsStructuredData()
    {
        var data = JsonSerializer.SerializeToElement(new { board = new { cells = new[] { "empty" } } });
        var log = new ProtocolLog(
            ProtocolConstants.Version,
            ProtocolConstants.LogType,
            DateTimeOffset.Parse("2026-07-24T01:00:00Z"),
            ProtocolLogLevel.Debug,
            "inspect",
            "req-1",
            Label: "scoredMoves",
            Data: data);

        var json = ProtocolJson.SerializeLine(log);
        var roundTrip = ProtocolJson.DeserializeLine<ProtocolLog>(json);

        Assert.Contains("\"label\":\"scoredMoves\"", json);
        Assert.Contains("\"data\":", json);
        Assert.Equal("scoredMoves", roundTrip.Label);
        Assert.NotNull(roundTrip.Data);
    }
}
