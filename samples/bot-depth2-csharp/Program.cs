using UttArena.BotSdk.CSharp;
using UttArena.Contracts;
using UttArena.GameEngine;

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

try
{
    await BotConsoleHost.RunAsync(new DepthTwoBot(), cancellationToken: shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
}

internal sealed class DepthTwoBot : IBot
{
    private const int WinScore = 1_000_000;
    private static readonly int[] PositionWeights = [3, 2, 3, 2, 5, 2, 3, 2, 3];
    private static readonly int[][] Lines =
    [
        [0, 1, 2], [3, 4, 5], [6, 7, 8],
        [0, 3, 6], [1, 4, 7], [2, 5, 8],
        [0, 4, 8], [2, 4, 6]
    ];

    public ValueTask<BotDecision> GetMoveAsync(
        MoveRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var state = ProtocolBridge.ToGameState(request);
        var me = ProtocolBridge.ToCell(request.BotMark);
        var legalMoves = request.LegalMoves.Select(ProtocolBridge.ToMove).ToArray();
        var best = legalMoves[0];
        var bestScore = int.MinValue;

        foreach (var move in legalMoves.OrderByDescending(MoveOrder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!UltimateTicTacToe.IsLegalMove(state, move))
            {
                continue;
            }

            var next = UltimateTicTacToe.ApplyMove(state, move);
            var score = WorstReply(next, me, cancellationToken);
            if (score > bestScore)
            {
                bestScore = score;
                best = move;
            }
        }

        return ValueTask.FromResult<BotDecision>(ProtocolBridge.ToPosition(best));
    }

    private static int WorstReply(
        GameState state,
        Cell me,
        CancellationToken cancellationToken)
    {
        if (state.IsTerminal)
        {
            return TerminalScore(state, me);
        }

        var worst = int.MaxValue;
        foreach (var reply in UltimateTicTacToe.GetLegalMoves(state).OrderByDescending(MoveOrder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var score = Evaluate(UltimateTicTacToe.ApplyMove(state, reply), me);
            worst = Math.Min(worst, score);
            if (worst <= -WinScore)
            {
                break;
            }
        }

        return worst == int.MaxValue ? Evaluate(state, me) : worst;
    }

    private static int Evaluate(GameState state, Cell me)
    {
        if (state.IsTerminal)
        {
            return TerminalScore(state, me);
        }

        var opponent = Opponent(me);
        var score = 0;
        for (var board = 0; board < 9; board++)
        {
            var result = state.GetSubBoardResult(board);
            if (result == WinFor(me))
            {
                score += 1_200 * PositionWeights[board];
            }
            else if (result == WinFor(opponent))
            {
                score -= 1_200 * PositionWeights[board];
            }
            else if (result == BoardResult.InProgress)
            {
                score += ScoreOpenBoard(state, board, me, opponent);
            }
        }

        score += ScoreMetaLines(state, me, opponent);
        if (state.ForcedBoard is null)
        {
            score += state.NextPlayer == me ? 80 : -80;
        }

        return score;
    }

    private static int ScoreOpenBoard(GameState state, int board, Cell me, Cell opponent)
    {
        var score = 0;
        for (var cell = 0; cell < 9; cell++)
        {
            var value = PositionWeights[cell];
            score += state.GetCell(board, cell) switch
            {
                var occupant when occupant == me => value,
                var occupant when occupant == opponent => -value,
                _ => 0
            };
        }

        foreach (var line in Lines)
        {
            var mine = line.Count(cell => state.GetCell(board, cell) == me);
            var theirs = line.Count(cell => state.GetCell(board, cell) == opponent);
            if (theirs == 0) score += mine * mine * 10;
            if (mine == 0) score -= theirs * theirs * 10;
        }

        return score;
    }

    private static int ScoreMetaLines(GameState state, Cell me, Cell opponent)
    {
        var score = 0;
        foreach (var line in Lines)
        {
            var mine = line.Count(board => state.GetSubBoardResult(board) == WinFor(me));
            var theirs = line.Count(board => state.GetSubBoardResult(board) == WinFor(opponent));
            if (theirs == 0) score += mine * mine * 500;
            if (mine == 0) score -= theirs * theirs * 500;
        }

        return score;
    }

    private static int MoveOrder(Move move) =>
        (PositionWeights[move.Cell] * 2) + PositionWeights[move.Board];

    private static int TerminalScore(GameState state, Cell me) => state.Result switch
    {
        BoardResult.Draw => 0,
        _ when state.Result == WinFor(me) => WinScore,
        _ => -WinScore
    };

    private static Cell Opponent(Cell player) => player == Cell.X ? Cell.O : Cell.X;

    private static BoardResult WinFor(Cell player) =>
        player == Cell.X ? BoardResult.XWin : BoardResult.OWin;
}
