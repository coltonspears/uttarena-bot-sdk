using System.Diagnostics;
using UttArena.Contracts;
using UttArena.GameEngine;

namespace UttArena.BotHarness;

public sealed record ConformanceCase(string Name, MoveRequest Request);

public sealed record ConformanceResult(string Name, bool Passed, long ElapsedMs, string? Detail);

/// <summary>
/// Replays a fixed set of positions and asserts the bot answers each one legally
/// and inside its budget. This catches the failure modes that only show up in an
/// arena match: unhandled forced boards, empty legal-move handling, and bots that
/// answer the wrong request id.
/// </summary>
public static class Conformance
{
    public static IReadOnlyList<ConformanceCase> Fixtures() =>
    [
        new("empty-board", Request(Position.Start())),
        new("forced-centre-board", Request(Position.Start().Play(new Move(4, 4)))),
        new("free-choice-after-closed-board", Request(FreeChoicePosition())),
        new("late-game-single-option", Request(CrowdedPosition())),
        new(
            "wildcard-special-available",
            Request(Position.Start(GameVariant.Wildcard).Play(new Move(4, 4)))),
        new(
            "double-move-special-available",
            Request(Position.Start(GameVariant.DoubleMove))),
        new(
            "double-move-pending",
            Request(Position.Start(GameVariant.DoubleMove).Play(
                new Move(4, 4), useSpecial: true)))
    ];

    public static async Task<IReadOnlyList<ConformanceResult>> RunAsync(
        string command,
        int moveTimeoutMs,
        CancellationToken cancellationToken)
    {
        var results = new List<ConformanceResult>();
        foreach (var fixture in Fixtures())
        {
            await using var bot = BotProcess.Start("candidate", command);
            // The deadline has to be stamped now, not when the fixture was built:
            // starting a child process for each case burns real time, and a bot that
            // honours the deadline would otherwise refuse an already-expired turn.
            var request = WithDeadlineFromNow(fixture.Request, moveTimeoutMs);
            var clock = Stopwatch.StartNew();
            try
            {
                var response = await bot.RequestMoveAsync(
                    request,
                    TimeSpan.FromMilliseconds(moveTimeoutMs + 5_000),
                    cancellationToken);
                clock.Stop();

                var validation = ProtocolValidation.ValidateMove(
                    request, response.Move, response.UseSpecial);
                if (!validation.IsValid)
                {
                    results.Add(new ConformanceResult(
                        fixture.Name, false, clock.ElapsedMilliseconds, string.Join("; ", validation.Errors)));
                }
                else if (clock.ElapsedMilliseconds > moveTimeoutMs)
                {
                    results.Add(new ConformanceResult(
                        fixture.Name,
                        false,
                        clock.ElapsedMilliseconds,
                        $"exceeded the {moveTimeoutMs} ms budget"));
                }
                else
                {
                    results.Add(new ConformanceResult(fixture.Name, true, clock.ElapsedMilliseconds, null));
                }
            }
            catch (Exception exception) when (
                exception is BotTimeoutException or BotProtocolException or ProtocolValidationException)
            {
                clock.Stop();
                results.Add(new ConformanceResult(
                    fixture.Name, false, clock.ElapsedMilliseconds, exception.Message));
            }
        }

        return results;
    }

    private static MoveRequest WithDeadlineFromNow(MoveRequest request, int moveTimeoutMs) =>
        request with
        {
            Timing = new MoveTiming(
                moveTimeoutMs,
                null,
                0,
                DateTimeOffset.UtcNow.AddMilliseconds(moveTimeoutMs))
        };

    private static MoveRequest Request(Position position, int moveTimeoutMs = 1_000)
    {
        var state = position.State;
        var protocolVariant = ProtocolBridge.ToProtocolVariant(state.Variant);
        var exposesSpecials = protocolVariant is
            ProtocolGameVariant.Wildcard or ProtocolGameVariant.DoubleMove;
        var specialMoves = exposesSpecials
            ? UltimateTicTacToe.GetLegalMoves(state, state.Variant, useSpecial: true)
            : Array.Empty<Move>();

        return new MoveRequest(
            ProtocolConstants.Version,
            ProtocolConstants.MoveRequestType,
            $"conformance-{state.Ply}",
            "conformance-match",
            ProtocolBridge.ToPlayerMark(state.NextPlayer),
            ProtocolBridge.ToBoardState(state),
            position.History,
            UltimateTicTacToe.GetLegalMoves(state, state.Variant, useSpecial: false)
                .Select(ProtocolBridge.ToPosition)
                .ToArray(),
            new OpponentMetadata("conformance", "Conformance harness", null),
            RulesetDescriptor.Standard,
            new MoveTiming(moveTimeoutMs, null, 0, DateTimeOffset.UtcNow.AddMilliseconds(moveTimeoutMs)),
            protocolVariant,
            exposesSpecials ? ProtocolBridge.ToSpecialAvailability(state) : null,
            state.DoubleMovePending,
            specialMoves.Count == 0
                ? null
                : specialMoves.Select(ProtocolBridge.ToPosition).ToArray());
    }

    // The first position whose target board is already decided, so the mover may
    // play anywhere. That free-choice rule is the one bots most often get wrong.
    private static Position FreeChoicePosition() =>
        Walk(position => position.State.ForcedBoard is null && position.State.Ply > 0);

    // A mid-game position reached by a deterministic legal walk, where the active
    // board has few open cells.
    private static Position CrowdedPosition() =>
        Walk(position => position.State.Ply >= 30);

    // Deterministic so the fixtures are identical on every machine and every run.
    private static Position Walk(Func<Position, bool> stop)
    {
        var position = Position.Start();
        for (var ply = 0; ply < 60 && !position.State.IsTerminal; ply++)
        {
            var moves = UltimateTicTacToe.GetLegalMoves(position.State);
            if (moves.Count == 0)
            {
                break;
            }

            position = position.Play(moves[((ply * 7) + 3) % moves.Count]);
            if (stop(position))
            {
                break;
            }
        }

        return position;
    }
}

/// <summary>
/// A game state plus the move history that produced it. The protocol requires the
/// two to agree, so fixtures cannot fabricate a state without its history.
/// </summary>
internal sealed record Position(GameState State, IReadOnlyList<MoveRecord> History)
{
    public static Position Start(GameVariant variant = GameVariant.Standard) =>
        new(GameState.Initial(variant), []);

    public Position Play(Move move, bool useSpecial = false)
    {
        var mark = ProtocolBridge.ToPlayerMark(State.NextPlayer);
        var next = UltimateTicTacToe.ApplyMove(
            State, move, State.Variant, seed: 0, useSpecial: useSpecial);
        return new Position(
            next,
            [.. History, new MoveRecord(next.Ply, mark, ProtocolBridge.ToPosition(move))]);
    }
}
