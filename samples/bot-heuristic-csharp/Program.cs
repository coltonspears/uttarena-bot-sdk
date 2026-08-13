using UttArena.BotSdk.CSharp;
using UttArena.Contracts;
using UttArena.GameEngine;

// Tier 2 reference bot: one-ply search with a positional heuristic.
//
// It looks at every legal move, plays it on a real engine copy, scores the result,
// and subtracts what the opponent could do in reply. No deeper search, so it is
// fast and never runs out of time, but it walks into two-move traps.

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

try
{
    await BotConsoleHost.RunAsync(new HeuristicBot(), cancellationToken: shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    // Ctrl+C is a normal shutdown path.
}

internal sealed class HeuristicBot : IBot
{
    // Cells and local boards on more winning lines are worth more: the centre is
    // on four lines, corners three, edges two.
    private static readonly int[] SquareWeights = [3, 2, 3, 2, 4, 2, 3, 2, 3];

    public ValueTask<BotDecision> GetMoveAsync(
        MoveRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var state = ProtocolBridge.ToGameState(request);
        var me = ProtocolBridge.ToCell(request.BotMark);
        var best = request.LegalMoves[0];
        var bestScore = int.MinValue;

        foreach (var candidate in request.LegalMoves)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var move = ProtocolBridge.ToMove(candidate);
            if (!UltimateTicTacToe.IsLegalMove(state, move))
            {
                continue;
            }

            var score = Score(state, move, me);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return ValueTask.FromResult<BotDecision>(best);
    }

    private static int Score(GameState state, Move move, Cell me)
    {
        var opponent = me == Cell.X ? Cell.O : Cell.X;
        var next = UltimateTicTacToe.ApplyMove(state, move);

        if (next.Result == WinFor(me))
        {
            return 1_000_000;
        }

        var score = SquareWeights[move.Cell] + (SquareWeights[move.Board] * 2);

        if (next.GetSubBoardResult(move.Board) == WinFor(me) &&
            state.GetSubBoardResult(move.Board) == BoardResult.InProgress)
        {
            score += 150;
        }

        // Handing the opponent a free choice of board is the classic blunder,
        // because it lets them pick their strongest position.
        if (next.ForcedBoard is null)
        {
            score -= 60;
        }

        foreach (var reply in UltimateTicTacToe.GetLegalMoves(next))
        {
            var afterReply = UltimateTicTacToe.ApplyMove(next, reply);
            if (afterReply.Result == WinFor(opponent))
            {
                return score - 500_000;
            }

            if (afterReply.GetSubBoardResult(reply.Board) == WinFor(opponent) &&
                next.GetSubBoardResult(reply.Board) == BoardResult.InProgress)
            {
                score -= 40;
            }
        }

        return score;
    }

    private static BoardResult WinFor(Cell player) =>
        player == Cell.X ? BoardResult.XWin : BoardResult.OWin;
}
