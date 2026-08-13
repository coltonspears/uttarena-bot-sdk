using System.Diagnostics;
using UttArena.BotSdk.CSharp;
using UttArena.Contracts;
using UttArena.GameEngine;

// Tier 4 reference bot: light Monte Carlo tree search with UCT selection and
// random rollouts.
//
// Unlike minimax this degrades gracefully: it spends a bounded slice of the
// available time and returns the most-visited child, so a tight deadline costs
// accuracy rather than producing a forfeit.

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

try
{
    await BotConsoleHost.RunAsync(new MctsBot(), cancellationToken: shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    // Ctrl+C is a normal shutdown path.
}

internal sealed class MctsBot : IBot
{
    private const double BudgetFraction = 0.6;
    private const int MaximumBudgetMs = 500;
    private const double Exploration = 1.41;
    private const int RolloutCap = 90;

    public ValueTask<BotDecision> GetMoveAsync(
        MoveRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var clock = Stopwatch.StartNew();
        var budget = Budget(request.Timing);
        var root = new Node(ProtocolBridge.ToGameState(request), null, default);

        while (clock.Elapsed < budget && !cancellationToken.IsCancellationRequested)
        {
            var node = Select(root);
            if (!node.State.IsTerminal)
            {
                node = Expand(node);
            }

            Backpropagate(node, Rollout(node.State));
        }

        var best = root.Children
            .OrderByDescending(child => child.Visits)
            .FirstOrDefault();
        return ValueTask.FromResult<BotDecision>(best is null
            ? request.LegalMoves[0]
            : ProtocolBridge.ToPosition(best.Move));
    }

    private static TimeSpan Budget(MoveTiming timing)
    {
        var untilDeadline = timing.DeadlineUtc - DateTimeOffset.UtcNow;
        var allowance = TimeSpan.FromMilliseconds(timing.MoveTimeoutMs);
        if (untilDeadline < allowance)
        {
            allowance = untilDeadline;
        }

        if (allowance <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        // In timed modes MoveTimeoutMs is the seat's whole remaining bank, not a
        // suggested per-turn spend. Cap this memory-growing search so it cannot
        // consume most of the clock or exhaust the container on an early move.
        var scaled = allowance * BudgetFraction;
        return scaled < TimeSpan.FromMilliseconds(MaximumBudgetMs)
            ? scaled
            : TimeSpan.FromMilliseconds(MaximumBudgetMs);
    }

    private static Node Select(Node node)
    {
        while (node.Untried.Count == 0 && node.Children.Count > 0)
        {
            var parentVisits = node.Visits;
            node = node.Children
                .OrderByDescending(child =>
                    (child.Wins / child.Visits) +
                    (Exploration * Math.Sqrt(Math.Log(parentVisits) / child.Visits)))
                .First();
        }

        return node;
    }

    private static Node Expand(Node node)
    {
        if (node.Untried.Count == 0)
        {
            return node;
        }

        var index = Random.Shared.Next(node.Untried.Count);
        var move = node.Untried[index];
        node.Untried.RemoveAt(index);

        var child = new Node(UltimateTicTacToe.ApplyMove(node.State, move), node, move);
        node.Children.Add(child);
        return child;
    }

    private static BoardResult Rollout(GameState state)
    {
        var current = state;
        for (var depth = 0; depth < RolloutCap && !current.IsTerminal; depth++)
        {
            var moves = UltimateTicTacToe.GetLegalMoves(current);
            if (moves.Count == 0)
            {
                break;
            }

            current = UltimateTicTacToe.ApplyMove(current, moves[Random.Shared.Next(moves.Count)]);
        }

        return current.Result;
    }

    // Each node stores its value from the perspective of whoever moved into it,
    // which is what makes the plain "highest win rate" UCT comparison correct.
    private static void Backpropagate(Node node, BoardResult result)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            current.Visits++;
            if (current.Parent is not null)
            {
                current.Wins += ScoreFor(result, current.Parent.State.NextPlayer);
            }
        }
    }

    private static double ScoreFor(BoardResult result, Cell player) => result switch
    {
        BoardResult.XWin => player == Cell.X ? 1 : 0,
        BoardResult.OWin => player == Cell.O ? 1 : 0,
        _ => 0.5
    };

    private sealed class Node
    {
        public Node(GameState state, Node? parent, Move move)
        {
            State = state;
            Parent = parent;
            Move = move;
            Untried = [.. UltimateTicTacToe.GetLegalMoves(state)];
        }

        public GameState State { get; }

        public Node? Parent { get; }

        public Move Move { get; }

        public List<Move> Untried { get; }

        public List<Node> Children { get; } = [];

        public int Visits { get; set; }

        public double Wins { get; set; }
    }
}
