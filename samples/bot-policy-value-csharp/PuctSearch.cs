using System.Diagnostics;
using UttArena.GameEngine;

namespace UttArena.PolicyValueBot;

internal readonly record struct SearchResult(int? Action, int Simulations, TimeSpan Elapsed);

internal sealed class PuctSearch
{
    private const float Exploration = 1.5f;
    private readonly int _maximumSimulations;
    private readonly PolicyValueNetwork _network;

    public PuctSearch(PolicyValueNetwork network, int maximumSimulations = 256)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumSimulations);

        _network = network;
        _maximumSimulations = maximumSimulations;
    }

    public SearchResult Search(
        GameState rootState,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rootState);
        var legalMoves = UltimateTicTacToe.GetLegalMoves(rootState);
        if (rootState.IsTerminal || legalMoves.Count == 0)
        {
            return new SearchResult(null, 0, TimeSpan.Zero);
        }

        var fallback = (legalMoves[0].Board * 9) + legalMoves[0].Cell;
        if (budget <= TimeSpan.Zero || _maximumSimulations == 0 || cancellationToken.IsCancellationRequested)
        {
            return new SearchResult(fallback, 0, TimeSpan.Zero);
        }

        var clock = Stopwatch.StartNew();
        var nodes = new Dictionary<GameState, Node>();
        var evaluationCache = new Dictionary<GameState, Evaluation>();
        var root = NodeFor(rootState, nodes);
        if (!TryExpand(root, evaluationCache, clock, budget, cancellationToken))
        {
            return new SearchResult(fallback, 0, clock.Elapsed);
        }

        var simulations = 0;
        while (simulations < _maximumSimulations &&
               clock.Elapsed < budget &&
               !cancellationToken.IsCancellationRequested)
        {
            var path = new List<PathStep>(32);
            var leaf = root;
            while (leaf.Expanded && !leaf.State.IsTerminal)
            {
                var edge = SelectEdge(leaf);
                edge.Child ??= NodeFor(
                    UltimateTicTacToe.ApplyMove(leaf.State, edge.Move),
                    nodes);
                path.Add(new PathStep(leaf, edge, edge.Child));
                leaf = edge.Child;
            }

            float value;
            if (leaf.State.IsTerminal)
            {
                value = TerminalValue(leaf.State);
            }
            else
            {
                if (!TryExpand(leaf, evaluationCache, clock, budget, cancellationToken))
                {
                    break;
                }

                value = leaf.InitialValue;
            }

            Backup(path, leaf, value);
            simulations++;
        }

        var selected = fallback;
        var bestVisits = -1;
        foreach (var edge in root.Edges)
        {
            var action = (edge.Move.Board * 9) + edge.Move.Cell;
            if (edge.VisitCount > bestVisits ||
                (edge.VisitCount == bestVisits && action < selected))
            {
                selected = action;
                bestVisits = edge.VisitCount;
            }
        }

        return new SearchResult(selected, simulations, clock.Elapsed);
    }

    private bool TryExpand(
        Node node,
        Dictionary<GameState, Evaluation> cache,
        Stopwatch clock,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        if (node.Expanded)
        {
            return true;
        }

        if (node.State.IsTerminal || clock.Elapsed >= budget || cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (!cache.TryGetValue(node.State, out var evaluation))
        {
            var features = FeatureEncoder.Encode(node.State);
            var output = _network.Evaluate(features);
            evaluation = new Evaluation(SoftmaxLegal(output.Logits, node.State), output.Value);
            cache.Add(node.State, evaluation);
        }

        var legalMoves = UltimateTicTacToe.GetLegalMoves(node.State);
        var edges = new Edge[legalMoves.Count];
        for (var index = 0; index < legalMoves.Count; index++)
        {
            var move = legalMoves[index];
            edges[index] = new Edge(move, evaluation.Priors[(move.Board * 9) + move.Cell]);
        }

        node.Edges = edges;
        node.InitialValue = evaluation.Value;
        node.Expanded = true;
        return true;
    }

    private static float[] SoftmaxLegal(float[] logits, GameState state)
    {
        var legalMoves = UltimateTicTacToe.GetLegalMoves(state);
        var priors = new float[PolicyValueNetwork.ActionCount];
        if (legalMoves.Count == 0)
        {
            return priors;
        }

        var maximum = float.NegativeInfinity;
        foreach (var move in legalMoves)
        {
            var action = (move.Board * 9) + move.Cell;
            maximum = MathF.Max(maximum, logits[action]);
        }

        double total = 0;
        foreach (var move in legalMoves)
        {
            var action = (move.Board * 9) + move.Cell;
            var prior = Math.Exp(logits[action] - maximum);
            if (!double.IsFinite(prior) || prior < 0)
            {
                prior = 0;
            }

            priors[action] = (float)prior;
            total += prior;
        }

        if (!(total > 0) || !double.IsFinite(total))
        {
            var uniform = 1f / legalMoves.Count;
            foreach (var move in legalMoves)
            {
                priors[(move.Board * 9) + move.Cell] = uniform;
            }

            return priors;
        }

        foreach (var move in legalMoves)
        {
            var action = (move.Board * 9) + move.Cell;
            priors[action] = (float)(priors[action] / total);
        }

        return priors;
    }

    private static Edge SelectEdge(Node node)
    {
        var explorationScale = Exploration * MathF.Sqrt(node.VisitCount + 1);
        Edge? best = null;
        var bestScore = float.NegativeInfinity;
        foreach (var edge in node.Edges)
        {
            var meanValue = edge.VisitCount == 0 ? 0 : edge.ValueSum / edge.VisitCount;
            var score = meanValue +
                (explorationScale * edge.Prior / (1 + edge.VisitCount));
            if (score > bestScore)
            {
                bestScore = score;
                best = edge;
            }
        }

        return best ?? throw new InvalidOperationException("Expanded node has no legal edges.");
    }

    private static void Backup(List<PathStep> path, Node leaf, float value)
    {
        leaf.VisitCount++;
        leaf.ValueSum += value;
        for (var index = path.Count - 1; index >= 0; index--)
        {
            var step = path[index];
            if (step.Parent.State.NextPlayer != step.Child.State.NextPlayer)
            {
                value = -value;
            }

            step.Edge.VisitCount++;
            step.Edge.ValueSum += value;
            step.Parent.VisitCount++;
            step.Parent.ValueSum += value;
        }
    }

    private static float TerminalValue(GameState state) => state.Result switch
    {
        BoardResult.Draw => 0,
        BoardResult.XWin => state.NextPlayer == Cell.X ? 1 : -1,
        BoardResult.OWin => state.NextPlayer == Cell.O ? 1 : -1,
        _ => throw new InvalidOperationException("A terminal value was requested for a live state."),
    };

    private static Node NodeFor(GameState state, Dictionary<GameState, Node> nodes)
    {
        if (!nodes.TryGetValue(state, out var node))
        {
            node = new Node(state);
            nodes.Add(state, node);
        }

        return node;
    }

    private sealed record Evaluation(float[] Priors, float Value);

    private readonly record struct PathStep(Node Parent, Edge Edge, Node Child);

    private sealed class Node(GameState state)
    {
        public GameState State { get; } = state;
        public Edge[] Edges { get; set; } = [];
        public bool Expanded { get; set; }
        public int VisitCount { get; set; }
        public float ValueSum { get; set; }
        public float InitialValue { get; set; }
    }

    private sealed class Edge(Move move, float prior)
    {
        public Move Move { get; } = move;
        public float Prior { get; } = prior;
        public Node? Child { get; set; }
        public int VisitCount { get; set; }
        public float ValueSum { get; set; }
    }
}
