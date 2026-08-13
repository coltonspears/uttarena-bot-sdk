using System.Globalization;
using UttArena.Contracts;

namespace UttArena.Contracts.Tests;

public sealed class ProtocolValidationCoverageTests
{
    [Fact]
    public void MatchStartValidationAcceptsCanonicalMessage()
    {
        var message = CreateMatchStart();
        var result = ProtocolValidation.Validate(message);
        Assert.True(result.IsValid);
        ProtocolValidation.EnsureValid(message);
    }

    [Fact]
    public void MatchStartValidationRejectsBadHeaderAndTiming()
    {
        var badType = CreateMatchStart() with { Type = "wrong" };
        var badTiming = CreateMatchStart() with
        {
            Timing = new TimingControl(0, null, -1)
        };

        Assert.False(ProtocolValidation.Validate(badType).IsValid);
        Assert.False(ProtocolValidation.Validate(badTiming).IsValid);
        Assert.Throws<ProtocolValidationException>(() => ProtocolValidation.EnsureValid(badType));
    }

    [Fact]
    public void MoveResponseValidationRejectsOutOfRangePosition()
    {
        var response = new MoveResponse(
            ProtocolConstants.Version,
            ProtocolConstants.MoveResponseType,
            "req",
            new BoardPosition(9, 0));

        var result = ProtocolValidation.Validate(response);
        Assert.False(result.IsValid);
        Assert.Throws<ProtocolValidationException>(
            () => ProtocolValidation.EnsureValid(response));
    }

    [Fact]
    public void EnsureValidMoveThrowsForIllegalSelection()
    {
        var request = CreateMoveRequest();
        Assert.Throws<ProtocolValidationException>(
            () => ProtocolValidation.EnsureValidMove(request, new BoardPosition(0, 0)));
        ProtocolValidation.EnsureValidMove(request, new BoardPosition(4, 4));
    }

    [Fact]
    public void SpecialMoveMustComeFromSeparateSpecialList()
    {
        var request = CreateMoveRequest() with
        {
            Variant = ProtocolGameVariant.Wildcard,
            SpecialAvailability = new SpecialAvailability(X: true, O: true),
            LegalSpecialMoves = [new BoardPosition(0, 0)]
        };

        Assert.True(ProtocolValidation.Validate(request).IsValid);
        ProtocolValidation.EnsureValidMove(
            request, new BoardPosition(0, 0), useSpecial: true);
        Assert.Throws<ProtocolValidationException>(() =>
            ProtocolValidation.EnsureValidMove(
                request, new BoardPosition(4, 4), useSpecial: true));
        Assert.Throws<ProtocolValidationException>(() =>
            ProtocolValidation.EnsureValidMove(
                request, new BoardPosition(0, 0), useSpecial: false));
    }

    [Fact]
    public void PendingDoubleMoveCannotOfferAnotherSpecial()
    {
        var request = CreateMoveRequest() with
        {
            Variant = ProtocolGameVariant.DoubleMove,
            SpecialAvailability = new SpecialAvailability(X: false, O: true),
            DoubleMovePending = true,
            LegalSpecialMoves = [new BoardPosition(4, 4)]
        };

        var result = ProtocolValidation.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains("pending", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ErrorAndLogMessagesRoundTrip()
    {
        var error = new ProtocolError(
            ProtocolConstants.Version,
            ProtocolConstants.ErrorType,
            "req",
            ProtocolErrorCode.InternalError,
            "boom",
            Fatal: true);
        var log = new ProtocolLog(
            ProtocolConstants.Version,
            ProtocolConstants.LogType,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
            ProtocolLogLevel.Error,
            "boom",
            "req");

        var errorJson = ProtocolJson.SerializeLine(error);
        var logJson = ProtocolJson.SerializeLine(log);

        Assert.Contains("\"type\":\"error\"", errorJson);
        Assert.Contains("\"fatal\":true", errorJson);
        Assert.Contains("\"type\":\"log\"", logJson);
        Assert.Equal(error.Code, ProtocolJson.DeserializeLine<ProtocolError>(errorJson).Code);
        Assert.Equal(log.Message, ProtocolJson.DeserializeLine<ProtocolLog>(logJson).Message);
    }

    private static MatchStartMessage CreateMatchStart() =>
        new(
            ProtocolConstants.Version,
            ProtocolConstants.MatchStartType,
            "match-1",
            PlayerMark.O,
            new OpponentMetadata("bot-x", "Opponent", null),
            RulesetDescriptor.Standard,
            new TimingControl(2_000, 60_000, 100));

    private static MoveRequest CreateMoveRequest()
    {
        var cells = Enumerable.Repeat(CellState.Empty, ProtocolConstants.CellCount).ToArray();
        var localBoards = Enumerable
            .Range(0, ProtocolConstants.LocalBoardCount)
            .Select(index => new LocalBoardState(index, LocalBoardStatus.Open))
            .ToArray();

        return new MoveRequest(
            ProtocolConstants.Version,
            ProtocolConstants.MoveRequestType,
            "request-1",
            "match-1",
            PlayerMark.X,
            new BoardState(cells, localBoards, 4, PlayerMark.X, 0),
            [],
            [new BoardPosition(4, 4)],
            new OpponentMetadata("bot-o", "Opponent", "1"),
            RulesetDescriptor.Standard,
            new MoveTiming(5_000, 30_000, 100, DateTimeOffset.UtcNow.AddMinutes(1)));
    }
}
