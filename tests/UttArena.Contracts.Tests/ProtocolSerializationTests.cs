using System.Text.Json;
using UttArena.Contracts;

namespace UttArena.Contracts.Tests;

public sealed class ProtocolSerializationTests
{
    [Fact]
    public void MoveRequest_RoundTripsAsSingleCamelCaseJsonLine()
    {
        var request = CreateRequest();

        var json = ProtocolJson.SerializeLine(request);
        var roundTrip = ProtocolJson.DeserializeLine<MoveRequest>(json);

        Assert.DoesNotContain('\n', json);
        Assert.DoesNotContain('\r', json);
        Assert.Contains("\"protocolVersion\":1", json);
        Assert.Contains("\"type\":\"move_request\"", json);
        Assert.Contains("\"botMark\":\"x\"", json);
        Assert.Contains("\"activeLocalBoard\":4", json);
        Assert.Equal(request.RequestId, roundTrip.RequestId);
        Assert.Equal(request.LegalMoves, roundTrip.LegalMoves);
        Assert.Equal(ProtocolConstants.CellCount, roundTrip.Board.Cells.Count);
        Assert.True(ProtocolValidation.Validate(roundTrip).IsValid);
    }

    [Fact]
    public void MoveResponse_EchoesRequestIdWithStableWireValues()
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
        Assert.True(ProtocolValidation.Validate(response).IsValid);
    }

    [Fact]
    public void StandardOptionalFieldsAreOmittedForBackwardCompatibility()
    {
        var requestJson = ProtocolJson.SerializeLine(CreateRequest());
        var responseJson = ProtocolJson.SerializeLine(new MoveResponse(
            ProtocolConstants.Version,
            ProtocolConstants.MoveResponseType,
            "request-17",
            new BoardPosition(4, 4)));

        Assert.DoesNotContain("\"variant\"", requestJson);
        Assert.DoesNotContain("\"specialAvailability\"", requestJson);
        Assert.DoesNotContain("\"doubleMovePending\"", requestJson);
        Assert.DoesNotContain("\"legalSpecialMoves\"", requestJson);
        Assert.DoesNotContain("\"randomSeed\"", requestJson);
        Assert.DoesNotContain("\"useSpecial\"", responseJson);
    }

    [Fact]
    public void SpecialVariantRequestAndResponseRoundTrip()
    {
        var request = CreateRequest() with
        {
            Variant = ProtocolGameVariant.Wildcard,
            SpecialAvailability = new SpecialAvailability(X: true, O: false),
            LegalSpecialMoves = [new BoardPosition(0, 0)]
        };
        var response = new MoveResponse(
            ProtocolConstants.Version,
            ProtocolConstants.MoveResponseType,
            request.RequestId,
            request.LegalSpecialMoves[0],
            UseSpecial: true);

        var requestJson = ProtocolJson.SerializeLine(request);
        var responseJson = ProtocolJson.SerializeLine(response);
        var requestRoundTrip = ProtocolJson.DeserializeLine<MoveRequest>(requestJson);
        var responseRoundTrip = ProtocolJson.DeserializeLine<MoveResponse>(responseJson);

        Assert.Contains("\"variant\":\"wildcard\"", requestJson);
        Assert.Contains("\"specialAvailability\":{\"x\":true,\"o\":false}", requestJson);
        Assert.Contains("\"legalSpecialMoves\"", requestJson);
        Assert.Contains("\"useSpecial\":true", responseJson);
        Assert.Equal(ProtocolGameVariant.Wildcard, requestRoundTrip.Variant);
        Assert.True(responseRoundTrip.UseSpecial);
        Assert.True(ProtocolValidation.Validate(requestRoundTrip).IsValid);
        Assert.True(ProtocolValidation.ValidateMove(
            requestRoundTrip, responseRoundTrip.Move, responseRoundTrip.UseSpecial).IsValid);
    }

    [Fact]
    public void ExtendedVariantAndRandomSeedRoundTripForSdk12()
    {
        var request = CreateRequest() with
        {
            Variant = ProtocolGameVariant.Chaos,
            RandomSeed = 42
        };
        var json = ProtocolJson.SerializeLine(request);
        var roundTrip = ProtocolJson.DeserializeLine<MoveRequest>(json);

        Assert.Contains("\"variant\":\"chaos\"", json);
        Assert.Contains("\"randomSeed\":42", json);
        Assert.Equal(ProtocolGameVariant.Chaos, roundTrip.Variant);
        Assert.Equal(42UL, roundTrip.RandomSeed);
    }

    [Fact]
    public void ProtocolCompatibilityGatesExtendedVariantsAt12()
    {
        Assert.True(ProtocolCompatibility.SupportsExtendedVariants("1.2.0"));
        Assert.True(ProtocolCompatibility.SupportsExtendedVariants("1.3.1"));
        Assert.False(ProtocolCompatibility.SupportsExtendedVariants("1.1.0"));
        Assert.False(ProtocolCompatibility.SupportsExtendedVariants(null));
        Assert.False(ProtocolCompatibility.SupportsExtendedVariants("not-a-version"));
    }

    [Fact]
    public void NumericEnumsAndUnknownPropertiesAreRejected()
    {
        const string numericMark =
            """{"protocolVersion":1,"type":"match_start","matchId":"m","botMark":1,"opponent":{"botId":"b","displayName":"Bot","version":null},"ruleset":{"id":"ultimate-tic-tac-toe","version":1,"boardSize":9,"localBoardSize":3,"sendMoveToLocalBoard":true,"freeMoveWhenTargetBoardClosed":true},"timing":{"moveTimeoutMs":1000,"initialBankMs":null,"incrementMs":0}}""";

        const string unknownProperty =
            """{"protocolVersion":1,"type":"move_response","requestId":"r","move":{"row":0,"column":0},"extra":true}""";

        Assert.Throws<JsonException>(() => ProtocolJson.DeserializeLine<MatchStartMessage>(numericMark));
        Assert.Throws<JsonException>(() => ProtocolJson.DeserializeLine<MoveResponse>(unknownProperty));
    }

    [Fact]
    public void ValidationRejectsMoveOutsideLegalMoves()
    {
        var request = CreateRequest();

        var result = ProtocolValidation.ValidateMove(request, new BoardPosition(0, 0));

        Assert.False(result.IsValid);
        Assert.Contains("move must be one of legalMoves.", result.Errors);
    }

    private static MoveRequest CreateRequest()
    {
        var cells = Enumerable
            .Repeat(CellState.Empty, ProtocolConstants.CellCount)
            .ToArray();
        var localBoards = Enumerable
            .Range(0, ProtocolConstants.LocalBoardCount)
            .Select(index => new LocalBoardState(index, LocalBoardStatus.Open))
            .ToArray();

        return new MoveRequest(
            ProtocolConstants.Version,
            ProtocolConstants.MoveRequestType,
            "request-17",
            "match-42",
            PlayerMark.X,
            new BoardState(
                cells,
                localBoards,
                ActiveLocalBoard: 4,
                CurrentTurn: PlayerMark.X,
                MoveNumber: 0),
            Array.Empty<MoveRecord>(),
            new[]
            {
                new BoardPosition(3, 3),
                new BoardPosition(4, 4),
                new BoardPosition(5, 5),
            },
            new OpponentMetadata("opponent-7", "Example Opponent", "2.1.0"),
            RulesetDescriptor.Standard,
            new MoveTiming(
                MoveTimeoutMs: 1_000,
                RemainingBankMs: 30_000,
                IncrementMs: 100,
                DeadlineUtc: DateTimeOffset.UtcNow.AddSeconds(1)));
    }
}
