using UttArena.BotSdk.CSharp;
using UttArena.Contracts;

namespace UttArena.BotSdk.Tests;

public sealed class BotConsoleHostTests
{
    [Fact]
    public async Task RunAsyncHandlesMatchStartAndLegalMoveRequest()
    {
        var bot = new FixedBot(new BoardPosition(4, 4));
        var input = new StringReader(string.Join(
            '\n',
            ProtocolJson.SerializeLine(CreateMatchStart()),
            ProtocolJson.SerializeLine(CreateMoveRequest()),
            ""));
        var output = new StringWriter();
        var error = new StringWriter();

        await BotConsoleHost.RunAsync(bot, input, output, error);

        Assert.Contains("\"type\":\"move_response\"", output.ToString());
        Assert.Contains("\"row\":4", output.ToString());
        Assert.Contains("\"type\":\"log\"", error.ToString());
    }

    [Fact]
    public async Task RunAsyncReportsUnsupportedProtocolVersion()
    {
        var bot = new FixedBot(new BoardPosition(0, 0));
        var input = new StringReader(
            """{"protocolVersion":99,"type":"match_start","matchId":"m","botMark":"x","opponent":{"botId":"b","displayName":"Bot","version":null},"ruleset":{"id":"ultimate-tic-tac-toe","version":1,"boardSize":9,"localBoardSize":3,"sendMoveToLocalBoard":true,"freeMoveWhenTargetBoardClosed":true},"timing":{"moveTimeoutMs":1000,"initialBankMs":null,"incrementMs":0}}""" + "\n");
        var output = new StringWriter();

        await BotConsoleHost.RunAsync(bot, input, output, new StringWriter());

        Assert.Contains("\"type\":\"error\"", output.ToString());
        Assert.Contains("unsupportedVersion", output.ToString());
    }

    [Fact]
    public async Task RunAsyncReportsMalformedJson()
    {
        var bot = new FixedBot(new BoardPosition(0, 0));
        var input = new StringReader("{not-json\n");
        var output = new StringWriter();

        await BotConsoleHost.RunAsync(bot, input, output, new StringWriter());

        Assert.Contains("malformedRequest", output.ToString());
    }

    [Fact]
    public async Task RunAsyncReportsIllegalBotMove()
    {
        var bot = new FixedBot(new BoardPosition(0, 0));
        var input = new StringReader(ProtocolJson.SerializeLine(CreateMoveRequest()) + "\n");
        var output = new StringWriter();

        await BotConsoleHost.RunAsync(bot, input, output, new StringWriter());

        Assert.Contains("illegalMove", output.ToString());
    }

    [Fact]
    public async Task BotConsoleLogWritesProtocolLogLine()
    {
        var error = new StringWriter();
        await BotConsoleLog.WriteAsync(ProtocolLogLevel.Debug, "hello", "req-1", error);

        var line = error.ToString().Trim();
        Assert.Contains("\"type\":\"log\"", line);
        Assert.Contains("\"message\":\"hello\"", line);
        Assert.Contains("\"requestId\":\"req-1\"", line);
    }

    [Fact]
    public async Task RunAsyncSerializesOptionalBanter()
    {
        var bot = new BanterBot(new BoardPosition(4, 4), "Nice board.");
        var input = new StringReader(ProtocolJson.SerializeLine(CreateMoveRequest()) + "\n");
        var output = new StringWriter();

        await BotConsoleHost.RunAsync(bot, input, output, new StringWriter());

        Assert.Contains("\"banter\":\"Nice board.\"", output.ToString());
    }

    [Fact]
    public async Task RunAsyncSerializesAndValidatesSpecialDecision()
    {
        var request = CreateMoveRequest() with
        {
            Variant = ProtocolGameVariant.Wildcard,
            SpecialAvailability = new SpecialAvailability(X: true, O: true),
            LegalSpecialMoves = [new BoardPosition(0, 0)]
        };
        var bot = new SpecialBot(new BoardPosition(0, 0));
        var output = new StringWriter();

        await BotConsoleHost.RunAsync(
            bot,
            new StringReader(ProtocolJson.SerializeLine(request) + "\n"),
            output,
            new StringWriter());

        var response = ProtocolJson.DeserializeLine<MoveResponse>(
            output.ToString().Trim());
        Assert.True(response.UseSpecial);
        Assert.Equal(new BoardPosition(0, 0), response.Move);
    }

    [Fact]
    public async Task OrdinaryDecisionKeepsLegacyResponseShape()
    {
        var output = new StringWriter();

        await BotConsoleHost.RunAsync(
            new FixedBot(new BoardPosition(4, 4)),
            new StringReader(ProtocolJson.SerializeLine(CreateMoveRequest()) + "\n"),
            output,
            new StringWriter());

        Assert.DoesNotContain("\"useSpecial\"", output.ToString());
    }

    [Fact]
    public async Task InspectAsyncWritesStructuredData()
    {
        var error = new StringWriter();
        await BotConsoleLog.InspectAsync("scored", new { score = 12 }, requestId: "req-1", error: error);

        var line = error.ToString().Trim();
        Assert.Contains("\"label\":\"scored\"", line);
        Assert.Contains("\"data\":", line);
        Assert.Contains("\"score\":12", line);
    }

    private static MatchStartMessage CreateMatchStart() =>
        new(
            ProtocolConstants.Version,
            ProtocolConstants.MatchStartType,
            "match-1",
            PlayerMark.X,
            new OpponentMetadata("bot-o", "Opponent", "1"),
            RulesetDescriptor.Standard,
            new TimingControl(1_000, null, 0));

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
            new BoardState(cells, localBoards, null, PlayerMark.X, 0),
            [],
            [new BoardPosition(4, 4)],
            new OpponentMetadata("bot-o", "Opponent", "1"),
            RulesetDescriptor.Standard,
            new MoveTiming(5_000, null, 0, DateTimeOffset.UtcNow.AddSeconds(30)));
    }

    private sealed class FixedBot(BoardPosition move) : IBot
    {
        public ValueTask<BotDecision> GetMoveAsync(
            MoveRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<BotDecision>(move);
    }

    private sealed class BanterBot(BoardPosition move, string banter) : IBot
    {
        public ValueTask<BotDecision> GetMoveAsync(
            MoveRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new BotDecision(move, banter));
    }

    private sealed class SpecialBot(BoardPosition move) : IBot
    {
        public ValueTask<BotDecision> GetMoveAsync(
            MoveRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new BotDecision(move, UseSpecial: true));
    }
}
