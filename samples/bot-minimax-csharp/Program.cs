using System.Diagnostics;
using UttArena.BotSdk.CSharp;
using UttArena.Contracts;
using UttArena.GameEngine;

// Tier 3 reference bot: iterative-deepening minimax with alpha-beta pruning.
//
// The time budget is the important part. The arena kills a bot that misses its
// deadline, so the search checks the clock at every node and the caller always
// keeps the best move from the last fully-searched depth.

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

try
{
    await BotConsoleHost.RunAsync(new MinimaxBot(), cancellationToken: shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    // Ctrl+C is a normal shutdown path.
}

internal sealed class MinimaxBot : IBot
{
    private const int WinScore = 1_000_000;
    private const int MaxDepth = 12;

    // Leave headroom for serialization and process scheduling on both sides.
    private const double BudgetFraction = 0.6;
    private const int MinimumBudgetMs = 20;

    private static readonly int[] SquareWeights = [3, 2, 3, 2, 4, 2, 3, 2, 3];
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

        var deadline = Stopwatch.StartNew();
        var budget = Budget(request.Timing);
        var state = ProtocolBridge.ToGameState(request);
        var me = ProtocolBridge.ToCell(request.BotMark);

        var rootMoves = UltimateTicTacToe.GetLegalMoves(state)
            .OrderByDescending(move => SquareWeights[move.Cell] + SquareWeights[move.Board])
            .ToArray();
        if (rootMoves.Length == 0)
        {
            return ValueTask.FromResult<BotDecision>(request.LegalMoves[0]);
        }

        var best = rootMoves[0];
        for (var depth = 1; depth <= MaxDepth; depth++)
        {
            var (move, completed) = SearchRoot(state, rootMoves, me, depth, deadline, budget, cancellationToken);
            if (!completed)
            {
                break;
            }

            best = move;
        }

        return ValueTask.FromResult<BotDecision>(ProtocolBridge.ToPosition(best));
    }

    private static TimeSpan Budget(MoveTiming timing)
    {
        var untilDeadline = timing.DeadlineUtc - DateTimeOffset.UtcNow;
        var allowance = TimeSpan.FromMilliseconds(timing.MoveTimeoutMs);
        if (untilDeadline > TimeSpan.Zero && untilDeadline < allowance)
        {
            allowance = untilDeadline;
        }

        var scaled = allowance * BudgetFraction;
        return scaled < TimeSpan.FromMilliseconds(MinimumBudgetMs)
            ? TimeSpan.FromMilliseconds(MinimumBudgetMs)
            : scaled;
    }

    private static (Move Move, bool Completed) SearchRoot(
        GameState state,
        Move[] moves,
        Cell me,
        int depth,
        Stopwatch clock,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        var best = moves[0];
        var bestScore = int.MinValue;

        foreach (var move in moves)
        {
            if (clock.Elapsed >= budget || cancellationToken.IsCancellationRequested)
            {
                return (best, false);
            }

            var score = Search(
                UltimateTicTacToe.ApplyMove(state, move),
                me,
                depth - 1,
                bestScore,
                int.MaxValue,
                clock,
                budget,
                out var aborted);
            if (aborted)
            {
                return (best, false);
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = move;
            }
        }

        return (best, true);
    }

    private static int Search(
        GameState state,
        Cell me,
        int depth,
        int alpha,
        int beta,
        Stopwatch clock,
        TimeSpan budget,
        out bool aborted)
    {
        aborted = false;
        if (clock.Elapsed >= budget)
        {
            aborted = true;
            return 0;
        }

        if (state.IsTerminal)
        {
            return Terminal(state, me, depth);
        }

        if (depth == 0)
        {
            return Evaluate(state, me);
        }

        var maximizing = state.NextPlayer == me;
        var best = maximizing ? int.MinValue : int.MaxValue;

        foreach (var move in UltimateTicTacToe.GetLegalMoves(state)
                     .OrderByDescending(candidate => SquareWeights[candidate.Cell]))
        {
            var score = Search(
                UltimateTicTacToe.ApplyMove(state, move),
                me,
                depth - 1,
                alpha,
                beta,
                clock,
                budget,
                out aborted);
            if (aborted)
            {
                return best == int.MinValue || best == int.MaxValue ? 0 : best;
            }

            if (maximizing)
            {
                best = Math.Max(best, score);
                alpha = Math.Max(alpha, best);
            }
            else
            {
                best = Math.Min(best, score);
                beta = Math.Min(beta, best);
            }

            if (beta <= alpha)
            {
                break;
            }
        }

        return best;
    }

    // Deeper wins score lower so the search prefers the fastest win and the
    // slowest loss instead of treating all wins as identical.
    private static int Terminal(GameState state, Cell me, int depth) => state.Result switch
    {
        BoardResult.Draw => 0,
        _ when state.Result == WinFor(me) => WinScore + depth,
        _ => -WinScore - depth
    };

    private static int Evaluate(GameState state, Cell me)
    {
        var opponent = me == Cell.X ? Cell.O : Cell.X;
        var score = 0;

        for (var board = 0; board < 9; board++)
        {
            var result = state.GetSubBoardResult(board);
            if (result == WinFor(me))
            {
                score += 120 * SquareWeights[board];
            }
            else if (result == WinFor(opponent))
            {
                score -= 120 * SquareWeights[board];
            }
            else if (result == BoardResult.InProgress)
            {
                score += LocalScore(state, board, me, opponent);
            }
        }

        score += MetaLineScore(state, me, opponent);
        if (state.ForcedBoard is null && state.NextPlayer != me)
        {
            score -= 40;
        }

        return score;
    }

    private static int LocalScore(GameState state, int board, Cell me, Cell opponent)
    {
        var score = 0;
        for (var cell = 0; cell < 9; cell++)
        {
            var occupant = state.GetCell(board, cell);
            if (occupant == me)
            {
                score += SquareWeights[cell];
            }
            else if (occupant == opponent)
            {
                score -= SquareWeights[cell];
            }
        }

        foreach (var line in Lines)
        {
            var mine = 0;
            var theirs = 0;
            foreach (var cell in line)
            {
                var occupant = state.GetCell(board, cell);
                if (occupant == me) mine++;
                else if (occupant == opponent) theirs++;
            }

            if (mine == 2 && theirs == 0) score += 12;
            if (theirs == 2 && mine == 0) score -= 12;
        }

        return score;
    }

    private static int MetaLineScore(GameState state, Cell me, Cell opponent)
    {
        var score = 0;
        foreach (var line in Lines)
        {
            var mine = 0;
            var theirs = 0;
            foreach (var board in line)
            {
                var result = state.GetSubBoardResult(board);
                if (result == WinFor(me)) mine++;
                else if (result == WinFor(opponent)) theirs++;
            }

            if (mine == 2 && theirs == 0) score += 500;
            if (theirs == 2 && mine == 0) score -= 500;
        }

        return score;
    }

    private static BoardResult WinFor(Cell player) =>
        player == Cell.X ? BoardResult.XWin : BoardResult.OWin;
}
