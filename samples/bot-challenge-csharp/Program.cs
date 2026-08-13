using System.Diagnostics;
using UttArena.BotSdk.CSharp;
using UttArena.Contracts;
using UttArena.GameEngine;

// Heavyweight challenge bot: iterative-deepening alpha-beta with meta-scorer
// evaluation, root tactical ordering, killers, and a move-hint transposition
// table. Built to be a hard human practice opponent and to outrank the sample
// minimax / random-rollout MCTS bots.

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

try
{
    await BotConsoleHost.RunAsync(new ChallengeBot(), cancellationToken: shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
}

internal sealed class ChallengeBot : IBot
{
    private const int WinScore = 1_000_000;
    private const int MaxDepth = 14;
    private const double BudgetFraction = 0.65;
    private const int MinimumBudgetMs = 20;
    private const int SoftCapBudgetMs = 2_500;
    private const int TtSize = 1 << 18;

    private static readonly int[] SquareWeights = [3, 2, 3, 2, 5, 2, 3, 2, 3];
    private static readonly int[][] Lines =
    [
        [0, 1, 2], [3, 4, 5], [6, 7, 8],
        [0, 3, 6], [1, 4, 7], [2, 5, 8],
        [0, 4, 8], [2, 4, 6]
    ];

    private static readonly ulong[][] ZobristCells = CreateCellKeys();
    private static readonly ulong[] ZobristSide = CreateSideKeys();
    private static readonly ulong[] ZobristForced = CreateForcedKeys();

    private readonly int[] _ttMove = new int[TtSize];
    private readonly ulong[] _ttKey = new ulong[TtSize];
    private readonly int[,] _killers = new int[64, 2];
    private readonly int[] _history = new int[81];

    public ValueTask<BotDecision> GetMoveAsync(
        MoveRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var clock = Stopwatch.StartNew();
        var budget = Budget(request.Timing);
        var state = ProtocolBridge.ToGameState(request);
        var me = ProtocolBridge.ToCell(request.BotMark);

        if (state.Ply == 0 && UltimateTicTacToe.IsLegalMove(state, new Move(4, 4)))
        {
            return ValueTask.FromResult(
                new BotDecision(ProtocolBridge.ToPosition(new Move(4, 4)), PickBanter(request)));
        }

        var rootMoves = UltimateTicTacToe.GetLegalMoves(state).ToArray();
        if (rootMoves.Length == 0)
        {
            return ValueTask.FromResult(new BotDecision(request.LegalMoves[0], PickBanter(request)));
        }

        // Instant game wins / forced blocks before burning the clock.
        foreach (var move in rootMoves)
        {
            var next = UltimateTicTacToe.ApplyMove(state, move);
            if (next.Result == WinFor(me))
            {
                return ValueTask.FromResult(
                    new BotDecision(ProtocolBridge.ToPosition(move), PickBanter(request)));
            }
        }

        if (rootMoves.Length == 1)
        {
            return ValueTask.FromResult(
                new BotDecision(ProtocolBridge.ToPosition(rootMoves[0]), PickBanter(request)));
        }

        Array.Clear(_killers);
        OrderRoot(state, rootMoves, me, ProbeHashMove(Hash(state)));

        var best = rootMoves[0];
        for (var depth = 1; depth <= MaxDepth; depth++)
        {
            var (move, completed) = SearchRoot(
                state, rootMoves, me, depth, clock, budget, cancellationToken);
            if (!completed)
            {
                break;
            }

            best = move;
            OrderRoot(state, rootMoves, me, Encode(best));
        }

        return ValueTask.FromResult(
            new BotDecision(ProtocolBridge.ToPosition(best), PickBanter(request)));
    }

    [Banter]
    private static string? PickBanter(MoveRequest request)
    {
        var situations = BanterSituations.Analyze(request);
        if (situations.HasFlag(BanterSituation.BotCanTakeLocalBoard))
        {
            return "Board claimed. Meta lines updated.";
        }

        if (situations.HasFlag(BanterSituation.OpponentJustWonLocalBoard))
        {
            return "You took one. The search keeps going.";
        }

        if (situations.HasFlag(BanterSituation.OpponentHasImmediateLocalThreat))
        {
            return "Threat on the board — calculating the block.";
        }

        if (situations.HasFlag(BanterSituation.FreeMove))
        {
            return "Free move. Routing matters more than the mark.";
        }

        if (situations.HasFlag(BanterSituation.Opening))
        {
            return "Opening: center pressure, then squeeze.";
        }

        if (situations.HasFlag(BanterSituation.Endgame))
        {
            return "Endgame — every tempo counts.";
        }

        return null;
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
        if (scaled > TimeSpan.FromMilliseconds(SoftCapBudgetMs))
        {
            scaled = TimeSpan.FromMilliseconds(SoftCapBudgetMs);
        }

        return scaled < TimeSpan.FromMilliseconds(MinimumBudgetMs)
            ? TimeSpan.FromMilliseconds(MinimumBudgetMs)
            : scaled;
    }

    private void OrderRoot(GameState state, Move[] moves, Cell me, int hashMove)
    {
        var opp = Opponent(me);
        var scores = new int[moves.Length];
        for (var i = 0; i < moves.Length; i++)
        {
            var move = moves[i];
            var code = Encode(move);
            var score = code == hashMove ? 1_000_000_000 : 0;
            var next = UltimateTicTacToe.ApplyMove(state, move);
            if (next.GetSubBoardResult(move.Board) == WinFor(me))
            {
                score += 800_000 + MetaUrgency(next, move.Board, me) * 100_000;
            }

            var givesOppWin = false;
            var oppTakes = 0;
            foreach (var reply in UltimateTicTacToe.GetLegalMoves(next))
            {
                var after = UltimateTicTacToe.ApplyMove(next, reply);
                if (after.Result == WinFor(opp))
                {
                    givesOppWin = true;
                    break;
                }

                if (next.GetSubBoardResult(reply.Board) == BoardResult.InProgress &&
                    after.GetSubBoardResult(reply.Board) == WinFor(opp))
                {
                    oppTakes++;
                }
            }

            if (givesOppWin)
            {
                score -= 2_000_000;
            }

            score -= oppTakes * 50_000;
            score += SquareWeights[move.Cell] * 10 + SquareWeights[move.Board] * 8;
            if (next.ForcedBoard is null)
            {
                score -= 30;
            }
            else if (HasLocalThreat(next, next.ForcedBoard.Value, me))
            {
                score += 120;
            }

            scores[i] = score;
        }

        Array.Sort(scores, moves);
        Array.Reverse(moves);
    }

    private (Move Move, bool Completed) SearchRoot(
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
                ply: 1,
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

        StoreHashMove(Hash(state), Encode(best));
        return (best, true);
    }

    private int Search(
        GameState state,
        Cell me,
        int depth,
        int alpha,
        int beta,
        int ply,
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
        var moves = UltimateTicTacToe.GetLegalMoves(state).ToArray();
        OrderInterior(moves, ProbeHashMove(Hash(state)), ply);

        var best = maximizing ? int.MinValue : int.MaxValue;
        var bestMove = Encode(moves[0]);

        foreach (var move in moves)
        {
            var score = Search(
                UltimateTicTacToe.ApplyMove(state, move),
                me,
                depth - 1,
                alpha,
                beta,
                ply + 1,
                clock,
                budget,
                out aborted);
            if (aborted)
            {
                return best is int.MinValue or int.MaxValue ? 0 : best;
            }

            if (maximizing)
            {
                if (score > best)
                {
                    best = score;
                    bestMove = Encode(move);
                }

                alpha = Math.Max(alpha, best);
            }
            else
            {
                if (score < best)
                {
                    best = score;
                    bestMove = Encode(move);
                }

                beta = Math.Min(beta, best);
            }

            if (beta <= alpha)
            {
                RememberKiller(ply, bestMove);
                _history[bestMove] = Math.Min(_history[bestMove] + depth * depth, 1_000_000);
                break;
            }
        }

        StoreHashMove(Hash(state), bestMove);
        return best;
    }

    private void OrderInterior(Move[] moves, int hashMove, int ply)
    {
        var scores = new int[moves.Length];
        for (var i = 0; i < moves.Length; i++)
        {
            var code = Encode(moves[i]);
            var score = code == hashMove ? 1_000_000 : SquareWeights[moves[i].Cell] * 10;
            if (ply < 64)
            {
                if (_killers[ply, 0] == code) score += 50_000;
                else if (_killers[ply, 1] == code) score += 40_000;
            }

            score += _history[code];
            scores[i] = score;
        }

        Array.Sort(scores, moves);
        Array.Reverse(moves);
    }

    private void RememberKiller(int ply, int moveCode)
    {
        if (ply >= 64 || moveCode < 0)
        {
            return;
        }

        if (_killers[ply, 0] != moveCode)
        {
            _killers[ply, 1] = _killers[ply, 0];
            _killers[ply, 0] = moveCode;
        }
    }

    private static int Terminal(GameState state, Cell me, int depth) => state.Result switch
    {
        BoardResult.Draw => 0,
        _ when state.Result == WinFor(me) => WinScore + depth,
        _ => -WinScore - depth
    };

    private static int Evaluate(GameState state, Cell me)
    {
        var opponent = Opponent(me);
        var score = 0;

        for (var board = 0; board < 9; board++)
        {
            var result = state.GetSubBoardResult(board);
            if (result == WinFor(me))
            {
                score += 3_000 * SquareWeights[board];
            }
            else if (result == WinFor(opponent))
            {
                score -= 3_000 * SquareWeights[board];
            }
            else if (result == BoardResult.InProgress)
            {
                score += LocalPotential(state, board, me, opponent);
            }
        }

        foreach (var line in Lines)
        {
            var mine = 0;
            var theirs = 0;
            var open = 0;
            foreach (var board in line)
            {
                var result = state.GetSubBoardResult(board);
                if (result == WinFor(me)) mine++;
                else if (result == WinFor(opponent)) theirs++;
                else if (result == BoardResult.InProgress) open++;
            }

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

        if (state.ForcedBoard is null)
        {
            if (state.NextPlayer == me) score += 400;
            else score -= 400;
        }
        else
        {
            var forced = state.ForcedBoard.Value;
            if (HasLocalThreat(state, forced, me) && state.NextPlayer == me) score += 1_200;
            if (HasLocalThreat(state, forced, opponent) && state.NextPlayer == opponent) score -= 1_400;
        }

        return score;
    }

    private static int LocalPotential(GameState state, int board, Cell me, Cell opponent)
    {
        var score = 0;
        foreach (var line in Lines)
        {
            var mine = 0;
            var theirs = 0;
            foreach (var cell in line)
            {
                var occ = state.GetCell(board, cell);
                if (occ == me) mine++;
                else if (occ == opponent) theirs++;
            }

            if (theirs == 0) score += mine * mine * 20;
            if (mine == 0) score -= theirs * theirs * 22;
        }

        for (var cell = 0; cell < 9; cell++)
        {
            var value = SquareWeights[cell];
            var occ = state.GetCell(board, cell);
            if (occ == me) score += value;
            else if (occ == opponent) score -= value;
        }

        return score;
    }

    private static int MetaUrgency(GameState state, int board, Cell player)
    {
        var urgency = 0;
        foreach (var line in Lines)
        {
            var mine = 0;
            var open = 0;
            var hit = false;
            foreach (var b in line)
            {
                if (b == board) hit = true;
                var result = state.GetSubBoardResult(b);
                if (result == WinFor(player)) mine++;
                else if (result == BoardResult.InProgress) open++;
            }

            if (hit && mine >= 2 && mine + open == 3)
            {
                urgency++;
            }
        }

        return urgency;
    }

    private static bool HasLocalThreat(GameState state, int board, Cell player)
    {
        if (state.GetSubBoardResult(board) != BoardResult.InProgress)
        {
            return false;
        }

        for (var cell = 0; cell < 9; cell++)
        {
            if (state.GetCell(board, cell) != Cell.Empty)
            {
                continue;
            }

            foreach (var line in Lines)
            {
                if (!Contains(line, cell))
                {
                    continue;
                }

                var count = 1;
                var blocked = false;
                foreach (var c in line)
                {
                    if (c == cell) continue;
                    var occ = state.GetCell(board, c);
                    if (occ == player) count++;
                    else if (occ != Cell.Empty)
                    {
                        blocked = true;
                        break;
                    }
                }

                if (!blocked && count == 3)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool Contains(int[] line, int value)
    {
        foreach (var item in line)
        {
            if (item == value) return true;
        }

        return false;
    }

    private int ProbeHashMove(ulong key)
    {
        var index = (int)(key & (TtSize - 1));
        return _ttKey[index] == key ? _ttMove[index] : -1;
    }

    private void StoreHashMove(ulong key, int move)
    {
        var index = (int)(key & (TtSize - 1));
        _ttKey[index] = key;
        _ttMove[index] = move;
    }

    private static ulong Hash(GameState state)
    {
        ulong h = 0;
        for (var i = 0; i < 81; i++)
        {
            h ^= ZobristCells[i][(int)state.Cells[i]];
        }

        h ^= ZobristSide[(int)state.NextPlayer];
        h ^= ZobristForced[state.ForcedBoard is int b ? b + 1 : 0];
        return h;
    }

    private static ulong[][] CreateCellKeys()
    {
        var rng = new Random(20260811);
        var table = new ulong[81][];
        for (var i = 0; i < 81; i++)
        {
            table[i] = new ulong[4];
            for (var c = 0; c < 4; c++)
            {
                table[i][c] = NextUlong(rng);
            }
        }

        return table;
    }

    private static ulong[] CreateSideKeys()
    {
        var rng = new Random(20260812);
        return [0UL, NextUlong(rng), NextUlong(rng), NextUlong(rng)];
    }

    private static ulong[] CreateForcedKeys()
    {
        var rng = new Random(20260813);
        var keys = new ulong[10];
        for (var i = 0; i < keys.Length; i++)
        {
            keys[i] = NextUlong(rng);
        }

        return keys;
    }

    private static ulong NextUlong(Random rng)
    {
        Span<byte> buffer = stackalloc byte[8];
        rng.NextBytes(buffer);
        return BitConverter.ToUInt64(buffer);
    }

    private static int Encode(Move move) => move.Board * 9 + move.Cell;

    private static Cell Opponent(Cell player) => player == Cell.X ? Cell.O : Cell.X;

    private static BoardResult WinFor(Cell player) =>
        player == Cell.X ? BoardResult.XWin : BoardResult.OWin;
}
