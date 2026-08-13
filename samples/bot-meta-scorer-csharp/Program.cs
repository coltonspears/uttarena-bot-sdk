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
    await BotConsoleHost.RunAsync(new MetaScorerBot(), cancellationToken: shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
}

internal sealed class MetaScorerBot : IBot
{
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
        var best = ProtocolBridge.ToMove(request.LegalMoves[0]);
        var bestScore = int.MinValue;

        foreach (var position in request.LegalMoves)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var move = ProtocolBridge.ToMove(position);
            if (!UltimateTicTacToe.IsLegalMove(state, move))
            {
                continue;
            }

            var next = UltimateTicTacToe.ApplyMove(state, move);
            var score = WorstReply(next, me, cancellationToken)
                + (PositionWeights[move.Cell] * 6)
                + (PositionWeights[move.Board] * 8);

            if (next.Result == WinFor(me))
            {
                score = 2_000_000;
            }

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
            return state.Result == WinFor(me) ? 2_000_000 : 0;
        }

        var opponent = Opponent(me);
        var worst = int.MaxValue;
        foreach (var reply in UltimateTicTacToe.GetLegalMoves(state))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var afterReply = UltimateTicTacToe.ApplyMove(state, reply);
            if (afterReply.Result == WinFor(opponent))
            {
                return -1_500_000;
            }

            worst = Math.Min(worst, ScorePosition(afterReply, me));
        }

        return worst == int.MaxValue ? ScorePosition(state, me) : worst;
    }

    private static int ScorePosition(GameState state, Cell me)
    {
        var opponent = Opponent(me);
        var score = 0;

        for (var board = 0; board < 9; board++)
        {
            var result = state.GetSubBoardResult(board);
            if (result == WinFor(me))
            {
                score += 3_000 * PositionWeights[board];
            }
            else if (result == WinFor(opponent))
            {
                score -= 3_000 * PositionWeights[board];
            }
            else if (result == BoardResult.InProgress)
            {
                score += LocalPotential(state, board, me, opponent);
            }
        }

        foreach (var line in Lines)
        {
            var mine = line.Count(board => state.GetSubBoardResult(board) == WinFor(me));
            var theirs = line.Count(board => state.GetSubBoardResult(board) == WinFor(opponent));
            var open = line.Count(board => state.GetSubBoardResult(board) == BoardResult.InProgress);

            if (theirs == 0)
            {
                score += mine switch
                {
                    2 when open == 1 => 80_000,
                    1 => 4_000,
                    _ => 200
                };
            }

            if (mine == 0)
            {
                score -= theirs switch
                {
                    2 when open == 1 => 90_000,
                    1 => 4_500,
                    _ => 200
                };
            }
        }

        return score;
    }

    private static int LocalPotential(GameState state, int board, Cell me, Cell opponent)
    {
        var score = 0;
        foreach (var line in Lines)
        {
            var mine = line.Count(cell => state.GetCell(board, cell) == me);
            var theirs = line.Count(cell => state.GetCell(board, cell) == opponent);

            if (theirs == 0) score += mine * mine * 20;
            if (mine == 0) score -= theirs * theirs * 22;
        }

        for (var cell = 0; cell < 9; cell++)
        {
            var value = PositionWeights[cell];
            if (state.GetCell(board, cell) == me) score += value;
            if (state.GetCell(board, cell) == opponent) score -= value;
        }

        return score;
    }

    private static Cell Opponent(Cell player) => player == Cell.X ? Cell.O : Cell.X;

    private static BoardResult WinFor(Cell player) =>
        player == Cell.X ? BoardResult.XWin : BoardResult.OWin;
}
