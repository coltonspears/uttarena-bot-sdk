using System.Diagnostics;
using UttArena.Contracts;
using UttArena.GameEngine;

namespace UttArena.BotHarness;

public enum GameOutcome
{
    XWin,
    OWin,
    Draw
}

public enum TurnFailureKind
{
    Timeout,
    IllegalMove,
    Protocol,
    Crash,
    Error,
}

public sealed record TurnFailure(
    string BotName,
    PlayerMark Mark,
    int Ply,
    TurnFailureKind Kind,
    string Reason);

public sealed record BotMoveTiming(IReadOnlyList<long> MoveTimesMs)
{
    public static BotMoveTiming Empty { get; } = new(Array.Empty<long>());
}

public sealed record GameReport(
    GameOutcome Outcome,
    int Plies,
    long LongestThinkMs,
    TurnFailure? Failure)
{
    public bool Forfeited => Failure is not null;
    public BotMoveTiming XTiming { get; init; } = BotMoveTiming.Empty;
    public BotMoveTiming OTiming { get; init; } = BotMoveTiming.Empty;
    public ulong Seed { get; init; }
    public IReadOnlyList<int> OpeningActions { get; init; } = Array.Empty<int>();
}

public sealed record GameSettings(
    int MoveTimeoutMs = 1_000,
    bool StrictTimeouts = true,
    GameVariant Variant = GameVariant.Standard,
    ulong Seed = 0,
    IReadOnlyList<int>? OpeningActions = null,
    GameState? InitialState = null,
    IReadOnlyList<MoveRecord>? InitialHistory = null,
    string? MatchId = null);

public sealed record PreparedGame(GameState State, IReadOnlyList<MoveRecord> History);

public static class GameSetup
{
    public static PreparedGame Prepare(GameSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.OpeningActions is not null && settings.InitialState is not null)
        {
            throw new ArgumentException(
                "OpeningActions and InitialState cannot both be supplied.", nameof(settings));
        }

        if (settings.InitialState is { } initial)
        {
            if (initial.Variant != settings.Variant)
            {
                throw new ArgumentException(
                    "InitialState variant must match GameSettings.Variant.", nameof(settings));
            }
            if (initial.IsTerminal)
            {
                throw new ArgumentException("InitialState cannot be terminal.", nameof(settings));
            }

            var suppliedHistory = settings.InitialHistory?.ToArray() ?? Array.Empty<MoveRecord>();
            if (suppliedHistory.Length != initial.Ply)
            {
                throw new ArgumentException(
                    "InitialHistory count must equal InitialState.Ply.", nameof(settings));
            }
            return new PreparedGame(initial, suppliedHistory);
        }

        var state = GameState.Initial(settings.Variant);
        var history = new List<MoveRecord>();
        foreach (var action in settings.OpeningActions ?? Array.Empty<int>())
        {
            if (action is < 0 or >= 81)
            {
                throw new ArgumentException(
                    $"Opening action {action} must be between 0 and 80.", nameof(settings));
            }

            var move = new Move(action / 9, action % 9);
            if (!UltimateTicTacToe.IsLegalMove(state, move, settings.Variant))
            {
                throw new ArgumentException(
                    $"Opening action {action} is illegal at ply {state.Ply}.", nameof(settings));
            }

            var mark = ProtocolBridge.ToPlayerMark(state.NextPlayer);
            state = UltimateTicTacToe.ApplyMove(
                state, move, settings.Variant, settings.Seed, useSpecial: false);
            history.Add(new MoveRecord(state.Ply, mark, ProtocolBridge.ToPosition(move)));
        }

        if (state.IsTerminal)
        {
            throw new ArgumentException("OpeningActions cannot produce a terminal state.", nameof(settings));
        }
        return new PreparedGame(state, history);
    }
}

/// <summary>
/// Plays one game between two child-process bots, adjudicated by the same engine
/// the arena uses. A bot that times out, crashes, or answers illegally forfeits,
/// which mirrors the arena's strictest timeout policy.
/// </summary>
public static class GameRunner
{
    public static async Task<GameReport> PlayAsync(
        BotProcess x,
        BotProcess o,
        GameSettings settings,
        CancellationToken cancellationToken)
    {
        var prepared = GameSetup.Prepare(settings);
        var state = prepared.State;
        var history = prepared.History.ToList();
        var xMoveTimes = new List<long>();
        var oMoveTimes = new List<long>();
        var longestThink = 0L;

        try
        {
            await AnnounceAsync(x, PlayerMark.X, o.Name, settings, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Forfeit(
                state, longestThink, x, PlayerMark.X, Classify(exception), exception.Message,
                xMoveTimes, oMoveTimes, settings);
        }

        try
        {
            await AnnounceAsync(o, PlayerMark.O, x.Name, settings, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Forfeit(
                state, longestThink, o, PlayerMark.O, Classify(exception), exception.Message,
                xMoveTimes, oMoveTimes, settings);
        }

        while (!state.IsTerminal)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var mark = ProtocolBridge.ToPlayerMark(state.NextPlayer);
            var bot = mark == PlayerMark.X ? x : o;
            var moveTimes = mark == PlayerMark.X ? xMoveTimes : oMoveTimes;
            var legalMoves = UltimateTicTacToe.GetLegalMoves(
                state, settings.Variant, useSpecial: false);
            if (legalMoves.Count == 0)
            {
                break;
            }

            var request = BuildRequest(state, history, legalMoves, mark, bot, settings);
            var clock = Stopwatch.StartNew();
            MoveResponse response;
            try
            {
                response = await bot.RequestMoveAsync(
                    request,
                    TimeSpan.FromMilliseconds(settings.MoveTimeoutMs + (settings.StrictTimeouts ? 50 : 5_000)),
                    cancellationToken);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException)
            {
                clock.Stop();
                moveTimes.Add(clock.ElapsedMilliseconds);
                longestThink = Math.Max(longestThink, clock.ElapsedMilliseconds);
                return Forfeit(
                    state, longestThink, bot, mark, Classify(exception), exception.Message,
                    xMoveTimes, oMoveTimes, settings);
            }

            clock.Stop();
            moveTimes.Add(clock.ElapsedMilliseconds);
            longestThink = Math.Max(longestThink, clock.ElapsedMilliseconds);

            var validation = ProtocolValidation.ValidateMove(
                request, response.Move, response.UseSpecial);
            if (!validation.IsValid)
            {
                return Forfeit(
                    state,
                    longestThink,
                    bot,
                    mark,
                    TurnFailureKind.IllegalMove,
                    $"{bot.Name} played an illegal move: {string.Join("; ", validation.Errors)}",
                    xMoveTimes,
                    oMoveTimes,
                    settings);
            }

            if (settings.StrictTimeouts && clock.ElapsedMilliseconds > settings.MoveTimeoutMs)
            {
                return Forfeit(
                    state,
                    longestThink,
                    bot,
                    mark,
                    TurnFailureKind.Timeout,
                    $"{bot.Name} took {clock.ElapsedMilliseconds} ms against a {settings.MoveTimeoutMs} ms budget",
                    xMoveTimes,
                    oMoveTimes,
                    settings);
            }

            var move = ProtocolBridge.ToMove(response.Move);
            if (!UltimateTicTacToe.IsLegalMove(
                state, move, settings.Variant, response.UseSpecial))
            {
                return Forfeit(
                    state,
                    longestThink,
                    bot,
                    mark,
                    TurnFailureKind.IllegalMove,
                    $"{bot.Name} played a move rejected by the game engine.",
                    xMoveTimes,
                    oMoveTimes,
                    settings);
            }

            state = UltimateTicTacToe.ApplyMove(
                state, move, settings.Variant, settings.Seed, useSpecial: response.UseSpecial);
            history.Add(new MoveRecord(state.Ply, mark, response.Move));
        }

        return CreateReport(
            ToOutcome(state.Result), state.Ply, longestThink, null,
            xMoveTimes, oMoveTimes, settings);
    }

    private static GameReport Forfeit(
        GameState state,
        long longestThink,
        BotProcess bot,
        PlayerMark mark,
        TurnFailureKind kind,
        string reason,
        IReadOnlyList<long> xMoveTimes,
        IReadOnlyList<long> oMoveTimes,
        GameSettings settings) =>
        CreateReport(
            mark == PlayerMark.X ? GameOutcome.OWin : GameOutcome.XWin,
            state.Ply,
            longestThink,
            new TurnFailure(bot.Name, mark, state.Ply, kind, reason),
            xMoveTimes,
            oMoveTimes,
            settings);

    private static GameReport CreateReport(
        GameOutcome outcome,
        int plies,
        long longestThink,
        TurnFailure? failure,
        IReadOnlyList<long> xMoveTimes,
        IReadOnlyList<long> oMoveTimes,
        GameSettings settings) =>
        new(outcome, plies, longestThink, failure)
        {
            XTiming = new BotMoveTiming(xMoveTimes.ToArray()),
            OTiming = new BotMoveTiming(oMoveTimes.ToArray()),
            Seed = settings.Seed,
            OpeningActions = settings.OpeningActions?.ToArray() ?? Array.Empty<int>(),
        };

    private static TurnFailureKind Classify(Exception exception) => exception switch
    {
        BotTimeoutException => TurnFailureKind.Timeout,
        BotCrashException => TurnFailureKind.Crash,
        BotProtocolException or ProtocolValidationException => TurnFailureKind.Protocol,
        _ => TurnFailureKind.Error,
    };

    private static Task AnnounceAsync(
        BotProcess bot,
        PlayerMark mark,
        string opponentName,
        GameSettings settings,
        CancellationToken cancellationToken) =>
        bot.SendAsync(
            new MatchStartMessage(
                ProtocolConstants.Version,
                ProtocolConstants.MatchStartType,
                MatchId(settings),
                mark,
                new OpponentMetadata("harness", opponentName, null),
                RulesetDescriptor.Standard,
                new TimingControl(settings.MoveTimeoutMs, null, 0)),
            cancellationToken);

    private static MoveRequest BuildRequest(
        GameState state,
        IReadOnlyList<MoveRecord> history,
        IReadOnlyList<Move> legalMoves,
        PlayerMark mark,
        BotProcess bot,
        GameSettings settings)
    {
        var protocolVariant = ProtocolBridge.ToProtocolVariant(settings.Variant);
        var exposesSpecials = protocolVariant is
            ProtocolGameVariant.Wildcard or ProtocolGameVariant.DoubleMove;
        var specialMoves = exposesSpecials
            ? UltimateTicTacToe.GetLegalMoves(state, settings.Variant, useSpecial: true)
            : Array.Empty<Move>();

        return new MoveRequest(
            ProtocolConstants.Version,
            ProtocolConstants.MoveRequestType,
            $"{MatchId(settings)}-turn-{state.Ply:D2}",
            MatchId(settings),
            mark,
            ProtocolBridge.ToBoardState(state),
            history,
            legalMoves.Select(ProtocolBridge.ToPosition).ToArray(),
            new OpponentMetadata("harness", bot.Name, null),
            RulesetDescriptor.Standard,
            new MoveTiming(
                settings.MoveTimeoutMs,
                null,
                0,
                DateTimeOffset.UtcNow.AddMilliseconds(settings.MoveTimeoutMs)),
            protocolVariant,
            exposesSpecials ? ProtocolBridge.ToSpecialAvailability(state) : null,
            state.DoubleMovePending,
            specialMoves.Count == 0
                ? null
                : specialMoves.Select(ProtocolBridge.ToPosition).ToArray(),
            settings.Variant == GameVariant.Chaos ? settings.Seed : null);
    }

    private static string MatchId(GameSettings settings) =>
        settings.MatchId ?? $"harness-{settings.Seed:x16}";

    private static GameOutcome ToOutcome(BoardResult result) => result switch
    {
        BoardResult.XWin => GameOutcome.XWin,
        BoardResult.OWin => GameOutcome.OWin,
        _ => GameOutcome.Draw
    };
}
