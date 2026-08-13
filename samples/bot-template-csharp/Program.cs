using UttArena.BotSdk.CSharp;
using UttArena.Contracts;

// The arena speaks newline-delimited JSON over stdin and stdout. BotConsoleHost
// owns that loop, enforces your move deadline, and validates your answer, so all
// you write is the move choice in MoveChooser below.
//
// stdout is reserved for protocol messages. Use BotConsoleLog (stderr) to debug,
// and BotConsoleLog.InspectAsync to dump objects into the unlisted Debug tab.
//
// Run `dotnet run -- --self-test` to check your bot answers a synthetic position
// legally without needing the arena or a test framework.

if (args.Contains("--self-test", StringComparer.Ordinal))
{
    return SelfTest.Run() ? 0 : 1;
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

try
{
    await BotConsoleHost.RunAsync(new MyBot(), cancellationToken: shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    // Ctrl+C is a normal shutdown path.
}

return 0;

internal sealed class MyBot : IBot
{
    public ValueTask<BotDecision> GetMoveAsync(
        MoveRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // request.LegalMoves is authoritative and is never empty on your turn.
        // Returning anything outside it forfeits the move.
        var move = MoveChooser.Choose(request);
        var banter = PickBanter(request);
        return ValueTask.FromResult(new BotDecision(move, banter));
    }

    [Banter]
    private static string? PickBanter(MoveRequest request)
    {
        var situations = BanterSituations.Analyze(request);
        if (situations.HasFlag(BanterSituation.BotCanTakeLocalBoard))
        {
            return "That board is mine.";
        }

        if (situations.HasFlag(BanterSituation.OpponentJustWonLocalBoard))
        {
            return "Nice board — the meta still matters.";
        }

        if (situations.HasFlag(BanterSituation.FreeMove))
        {
            return "Anywhere works. Choosing carefully.";
        }

        return null;
    }
}

// Kept separate from the entry point so the self-test can exercise it directly.
internal static class MoveChooser
{
    public static BoardPosition Choose(MoveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Prefer the centre of a local board when available: it sits on four of
        // the eight winning lines instead of two or three.
        foreach (var move in request.LegalMoves)
        {
            if (move.Row % 3 == 1 && move.Column % 3 == 1)
            {
                return move;
            }
        }

        return request.LegalMoves[0];
    }
}

internal static class SelfTest
{
    public static bool Run()
    {
        var request = OpeningPosition();
        var move = MoveChooser.Choose(request);

        if (!request.LegalMoves.Contains(move))
        {
            Console.Error.WriteLine($"FAIL: ({move.Row}, {move.Column}) is not a legal move.");
            return false;
        }

        var validation = ProtocolValidation.ValidateMove(request, move);
        if (!validation.IsValid)
        {
            Console.Error.WriteLine($"FAIL: {string.Join("; ", validation.Errors)}");
            return false;
        }

        Console.Error.WriteLine($"PASS: chose ({move.Row}, {move.Column}) on an empty board.");
        return true;
    }

    private static MoveRequest OpeningPosition()
    {
        var cells = new CellState[ProtocolConstants.CellCount];
        Array.Fill(cells, CellState.Empty);
        var localBoards = Enumerable.Range(0, ProtocolConstants.LocalBoardCount)
            .Select(index => new LocalBoardState(index, LocalBoardStatus.Open))
            .ToArray();
        var legalMoves = Enumerable.Range(0, ProtocolConstants.CellCount)
            .Select(index => new BoardPosition(
                index / ProtocolConstants.BoardSize,
                index % ProtocolConstants.BoardSize))
            .ToArray();

        return new MoveRequest(
            ProtocolConstants.Version,
            ProtocolConstants.MoveRequestType,
            "self-test",
            "self-test-match",
            PlayerMark.X,
            new BoardState(cells, localBoards, null, PlayerMark.X, 0),
            [],
            legalMoves,
            new OpponentMetadata("self-test", "Self test", null),
            RulesetDescriptor.Standard,
            new MoveTiming(1000, null, 0, DateTimeOffset.UtcNow.AddSeconds(1)));
    }
}
