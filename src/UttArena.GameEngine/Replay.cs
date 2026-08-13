using System.Collections.ObjectModel;

namespace UttArena.GameEngine;

public sealed record MatchDefinition
{
    public MatchDefinition(
        string matchId,
        string xParticipant,
        string oParticipant,
        Ruleset ruleset,
        TimeControl timeControl,
        FailurePolicy failurePolicy,
        ResourceProfile resourceProfile,
        ulong randomSeed,
        string prngName,
        int prngVersion,
        string initialStateHash)
    {
        MatchId = Required(matchId, nameof(matchId));
        XParticipant = Required(xParticipant, nameof(xParticipant));
        OParticipant = Required(oParticipant, nameof(oParticipant));
        Ruleset = ruleset ?? throw new ArgumentNullException(nameof(ruleset));
        TimeControl = timeControl ?? throw new ArgumentNullException(nameof(timeControl));
        FailurePolicy = failurePolicy ?? throw new ArgumentNullException(nameof(failurePolicy));
        ResourceProfile = resourceProfile ?? throw new ArgumentNullException(nameof(resourceProfile));
        RandomSeed = randomSeed;
        PrngName = Required(prngName, nameof(prngName));
        PrngVersion = prngVersion > 0
            ? prngVersion
            : throw new ArgumentOutOfRangeException(nameof(prngVersion));
        InitialStateHash = Required(initialStateHash, nameof(initialStateHash));
    }

    public string MatchId { get; }
    public string XParticipant { get; }
    public string OParticipant { get; }
    public Ruleset Ruleset { get; }
    public TimeControl TimeControl { get; }
    public FailurePolicy FailurePolicy { get; }
    public ResourceProfile ResourceProfile { get; }
    public ulong RandomSeed { get; }
    public string PrngName { get; }
    public int PrngVersion { get; }
    public string InitialStateHash { get; }

    public static MatchDefinition Create(
        string matchId,
        string xParticipant,
        string oParticipant,
        ulong randomSeed = 0) =>
        new(
            matchId,
            xParticipant,
            oParticipant,
            Ruleset.Standard,
            TimeControl.Untimed,
            FailurePolicy.Strict,
            ResourceProfile.Default,
            randomSeed,
            DeterministicPrng.AlgorithmName,
            DeterministicPrng.AlgorithmVersion,
            StateHasher.Compute(GameState.Initial()));

    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", name)
            : value;
}

public abstract record MatchEvent(long Sequence);

public sealed record MoveAppliedEvent(
    long Sequence,
    Move Move,
    Cell Player,
    string BeforeStateHash,
    string AfterStateHash) : MatchEvent(Sequence);

public sealed record MatchCompletedEvent(
    long Sequence,
    BoardResult Result,
    string FinalStateHash) : MatchEvent(Sequence);

public sealed class MatchRecord
{
    private readonly ReadOnlyCollection<MatchEvent> _events;

    public MatchRecord(MatchDefinition definition)
        : this(definition, Array.Empty<MatchEvent>())
    {
    }

    private MatchRecord(MatchDefinition definition, IEnumerable<MatchEvent> events)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _events = Array.AsReadOnly(events.ToArray());
    }

    public MatchDefinition Definition { get; }
    public IReadOnlyList<MatchEvent> Events => _events;

    public MatchRecord Append(MatchEvent matchEvent)
    {
        ArgumentNullException.ThrowIfNull(matchEvent);
        if (matchEvent.Sequence != _events.Count)
        {
            throw new InvalidOperationException(
                $"Expected event sequence {_events.Count}, but received {matchEvent.Sequence}.");
        }

        if (_events.LastOrDefault() is MatchCompletedEvent)
        {
            throw new InvalidOperationException("A completed match cannot accept more events.");
        }

        return new MatchRecord(Definition, _events.Append(matchEvent));
    }
}

public static class MatchEventFactory
{
    public static MoveAppliedEvent CreateMove(long sequence, GameState state, Move move)
    {
        ArgumentNullException.ThrowIfNull(state);
        var after = UltimateTicTacToe.ApplyMove(state, move);
        return new MoveAppliedEvent(
            sequence,
            move,
            state.NextPlayer,
            StateHasher.Compute(state),
            StateHasher.Compute(after));
    }

    public static MatchCompletedEvent CreateCompletion(long sequence, GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.IsTerminal)
        {
            throw new InvalidOperationException("Only a terminal state can complete a match.");
        }

        return new MatchCompletedEvent(sequence, state.Result, StateHasher.Compute(state));
    }
}

public sealed record ReplayResult(GameState FinalState, BoardResult Result, int AppliedMoves);

public sealed class ReplayValidationException : Exception
{
    public ReplayValidationException(string message)
        : base(message)
    {
    }

    public ReplayValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class ReplayService
{
    public ReplayResult Replay(MatchRecord match)
    {
        ArgumentNullException.ThrowIfNull(match);
        return Replay(match.Definition, match.Events);
    }

    public ReplayResult Replay(
        MatchDefinition definition,
        IEnumerable<MatchEvent> events)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(events);

        ValidateDefinition(definition);
        var state = GameState.Initial();
        EnsureHash(
            definition.InitialStateHash,
            StateHasher.Compute(state),
            "Match definition initial state hash");

        var expectedSequence = 0L;
        var appliedMoves = 0;
        var completed = false;

        foreach (var matchEvent in events)
        {
            if (matchEvent is null)
            {
                throw new ReplayValidationException("The event stream contains a null event.");
            }

            if (completed)
            {
                throw new ReplayValidationException("Events appear after match completion.");
            }

            if (matchEvent.Sequence != expectedSequence)
            {
                throw new ReplayValidationException(
                    $"Expected event sequence {expectedSequence}, but found {matchEvent.Sequence}.");
            }

            switch (matchEvent)
            {
                case MoveAppliedEvent moveEvent:
                    EnsureHash(
                        moveEvent.BeforeStateHash,
                        StateHasher.Compute(state),
                        $"Event {expectedSequence} before-state hash");
                    if (moveEvent.Player != state.NextPlayer)
                    {
                        throw new ReplayValidationException(
                            $"Event {expectedSequence} names the wrong player.");
                    }

                    try
                    {
                        state = UltimateTicTacToe.ApplyMove(state, moveEvent.Move);
                    }
                    catch (Exception exception)
                        when (exception is InvalidOperationException or ArgumentOutOfRangeException)
                    {
                        throw new ReplayValidationException(
                            $"Event {expectedSequence} contains an illegal move.",
                            exception);
                    }

                    EnsureHash(
                        moveEvent.AfterStateHash,
                        StateHasher.Compute(state),
                        $"Event {expectedSequence} after-state hash");
                    appliedMoves++;
                    break;

                case MatchCompletedEvent completionEvent:
                    if (!state.IsTerminal)
                    {
                        throw new ReplayValidationException(
                            "The completion event occurs before the game is terminal.");
                    }

                    if (completionEvent.Result != state.Result)
                    {
                        throw new ReplayValidationException(
                            "The completion event result does not match the replayed state.");
                    }

                    EnsureHash(
                        completionEvent.FinalStateHash,
                        StateHasher.Compute(state),
                        "Completion final-state hash");
                    completed = true;
                    break;

                default:
                    throw new ReplayValidationException(
                        $"Unsupported event type {matchEvent.GetType().Name}.");
            }

            expectedSequence++;
        }

        if (state.IsTerminal && !completed)
        {
            throw new ReplayValidationException("A terminal game is missing its completion event.");
        }

        return new ReplayResult(state, state.Result, appliedMoves);
    }

    private static void ValidateDefinition(MatchDefinition definition)
    {
        if (definition.Ruleset != Ruleset.Standard)
        {
            throw new ReplayValidationException(
                $"Unsupported ruleset {definition.Ruleset.Name} v{definition.Ruleset.Version}.");
        }

        if (definition.PrngName != DeterministicPrng.AlgorithmName
            || definition.PrngVersion != DeterministicPrng.AlgorithmVersion)
        {
            throw new ReplayValidationException(
                $"Unsupported PRNG {definition.PrngName} v{definition.PrngVersion}.");
        }
    }

    private static void EnsureHash(string recorded, string actual, string context)
    {
        if (!string.Equals(recorded, actual, StringComparison.Ordinal))
        {
            throw new ReplayValidationException($"{context} does not match.");
        }
    }
}
