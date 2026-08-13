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
    await BotConsoleHost.RunAsync(new ThreatGreedyBot(), cancellationToken: shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
}

internal sealed class ThreatGreedyBot : IBot
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
        var bestMove = ProtocolBridge.ToMove(request.LegalMoves[0]);
        var bestScore = int.MinValue;

        foreach (var position in request.LegalMoves)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var move = ProtocolBridge.ToMove(position);
            if (!UltimateTicTacToe.IsLegalMove(state, move))
            {
                continue;
            }

            var score = ScoreMove(state, move, me);
            if (score > bestScore)
            {
                bestScore = score;
                bestMove = move;
            }
        }

        return ValueTask.FromResult<BotDecision>(ProtocolBridge.ToPosition(bestMove));
    }

    private static int ScoreMove(GameState state, Move move, Cell me)
    {
        var opponent = Opponent(me);
        var before = state.GetSubBoardResult(move.Board);
        var next = UltimateTicTacToe.ApplyMove(state, move);

        if (next.Result == WinFor(me))
        {
            return 2_000_000;
        }

        var score = (PositionWeights[move.Cell] * 8) + (PositionWeights[move.Board] * 3);
        if (before == BoardResult.InProgress && next.GetSubBoardResult(move.Board) == WinFor(me))
        {
            score += 20_000 + (MetaUrgency(next, move.Board, me) * 2_000);
        }

        score += LocalLinePressure(next, move.Board, me, opponent);
        if (next.ForcedBoard is null)
        {
            score -= 250;
        }

        foreach (var reply in UltimateTicTacToe.GetLegalMoves(next))
        {
            var afterReply = UltimateTicTacToe.ApplyMove(next, reply);
            if (afterReply.Result == WinFor(opponent))
            {
                return -1_500_000;
            }

            if (next.GetSubBoardResult(reply.Board) == BoardResult.InProgress &&
                afterReply.GetSubBoardResult(reply.Board) == WinFor(opponent))
            {
                score -= 16_000 + (MetaUrgency(afterReply, reply.Board, opponent) * 1_500);
            }
        }

        return score;
    }

    private static int LocalLinePressure(GameState state, int board, Cell me, Cell opponent)
    {
        var score = 0;
        foreach (var line in Lines)
        {
            var mine = line.Count(cell => state.GetCell(board, cell) == me);
            var theirs = line.Count(cell => state.GetCell(board, cell) == opponent);
            if (theirs == 0)
            {
                score += mine * mine * 12;
            }
            else if (mine == 0)
            {
                score -= theirs * theirs * 14;
            }
        }

        return score;
    }

    private static int MetaUrgency(GameState state, int board, Cell player) =>
        Lines.Count(line =>
            line.Contains(board) &&
            line.Count(index => state.GetSubBoardResult(index) == WinFor(player)) >= 2);

    private static Cell Opponent(Cell player) => player == Cell.X ? Cell.O : Cell.X;

    private static BoardResult WinFor(Cell player) =>
        player == Cell.X ? BoardResult.XWin : BoardResult.OWin;
}
