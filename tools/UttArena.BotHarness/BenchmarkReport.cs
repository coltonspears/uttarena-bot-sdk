using System.Text.Json;
using System.Text.Json.Serialization;

namespace UttArena.BotHarness;

public sealed record BenchmarkGameRecord(
    int Index,
    string MatchId,
    string OpeningId,
    IReadOnlyList<int> OpeningMoves,
    bool FirstPlayedX,
    ulong GameSeed,
    GameReport Report);

public sealed record TimingSummary(
    int Moves,
    long TotalMs,
    double MeanMs,
    long P50Ms,
    long P95Ms,
    long MaxMs)
{
    public static TimingSummary FromSamples(IEnumerable<long> samples)
    {
        var ordered = samples.Order().ToArray();
        if (ordered.Length == 0)
        {
            return new TimingSummary(0, 0, 0, 0, 0, 0);
        }

        var total = ordered.Sum();
        return new TimingSummary(
            ordered.Length,
            total,
            Math.Round(total / (double)ordered.Length, 3),
            Percentile(ordered, 0.50),
            Percentile(ordered, 0.95),
            ordered[^1]);
    }

    private static long Percentile(long[] ordered, double percentile)
    {
        var index = Math.Max(0, (int)Math.Ceiling(percentile * ordered.Length) - 1);
        return ordered[index];
    }
}

public sealed record ReportCommands(string First, string Second);

public sealed record ReportOptions(
    int? RequestedGames,
    int Games,
    int? OpeningCount,
    string? OpeningsPath,
    int MoveTimeoutMs,
    bool StrictTimeouts,
    string Variant,
    ulong Seed,
    bool PairedOpenings);

public sealed record ReportOpening(string Id, IReadOnlyList<int> Moves);

public sealed record ReportColorAssignment(string X, string O);

public sealed record ReportFailure(
    string Bot,
    string Mark,
    int Ply,
    string Category,
    string Reason);

public sealed record ReportTiming(TimingSummary First, TimingSummary Second);

public sealed record ReportGame(
    int Index,
    string MatchId,
    ReportOpening Opening,
    ReportColorAssignment ColorAssignment,
    ulong GameSeed,
    string Outcome,
    string? Winner,
    int Plies,
    ReportTiming Timing,
    bool Forfeit,
    bool IllegalMove,
    bool Timeout,
    string? FailureCategory,
    ReportFailure? Failure);

public sealed record BotAggregate(
    int Wins,
    int Draws,
    int Losses,
    double Score,
    int Forfeits,
    int IllegalMoves,
    int Timeouts,
    int ProtocolFailures,
    int CrashErrors,
    TimingSummary Timing);

public sealed record ReportAggregate(
    int Games,
    int Forfeits,
    int IllegalMoves,
    int Timeouts,
    int ProtocolFailures,
    int CrashErrors,
    BotAggregate First,
    BotAggregate Second);

public sealed record HarnessJsonReport(
    string Schema,
    int Version,
    ReportCommands Commands,
    ReportOptions Options,
    IReadOnlyList<ReportGame> Games,
    ReportAggregate Aggregate);

public static class HarnessReport
{
    public const string SchemaName = "uttarena.bot-harness-report";
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static HarnessJsonReport Create(
        HarnessOptions options,
        IReadOnlyList<BenchmarkGameRecord> records,
        int? openingCount)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(records);

        var games = records.Select(ToReportGame).ToArray();
        var aggregate = Aggregate(records);
        return new HarnessJsonReport(
            SchemaName,
            SchemaVersion,
            new ReportCommands(options.XCommand, options.OCommand),
            new ReportOptions(
                options.OpeningsPath is null ? options.Games : null,
                records.Count,
                openingCount,
                options.OpeningsPath,
                options.MoveTimeoutMs,
                options.StrictTimeouts,
                VariantName(options.Variant),
                options.Seed,
                options.OpeningsPath is not null),
            games,
            aggregate);
    }

    public static ReportAggregate Aggregate(IReadOnlyList<BenchmarkGameRecord> records)
    {
        var firstWins = 0;
        var secondWins = 0;
        var draws = 0;
        var firstForfeits = 0;
        var secondForfeits = 0;
        var firstIllegalMoves = 0;
        var secondIllegalMoves = 0;
        var firstTimeouts = 0;
        var secondTimeouts = 0;
        var firstProtocols = 0;
        var secondProtocols = 0;
        var firstCrashErrors = 0;
        var secondCrashErrors = 0;
        var firstTimes = new List<long>();
        var secondTimes = new List<long>();

        foreach (var game in records)
        {
            var firstWon = game.Report.Outcome switch
            {
                GameOutcome.XWin => game.FirstPlayedX,
                GameOutcome.OWin => !game.FirstPlayedX,
                _ => false,
            };
            if (game.Report.Outcome == GameOutcome.Draw)
            {
                draws++;
            }
            else if (firstWon)
            {
                firstWins++;
            }
            else
            {
                secondWins++;
            }

            firstTimes.AddRange(
                game.FirstPlayedX ? game.Report.XTiming.MoveTimesMs : game.Report.OTiming.MoveTimesMs);
            secondTimes.AddRange(
                game.FirstPlayedX ? game.Report.OTiming.MoveTimesMs : game.Report.XTiming.MoveTimesMs);

            if (game.Report.Failure is not { } failure)
            {
                continue;
            }

            var firstFailed = failure.Mark == (game.FirstPlayedX
                ? UttArena.Contracts.PlayerMark.X
                : UttArena.Contracts.PlayerMark.O);
            IncrementFailure(
                failure.Kind,
                firstFailed,
                ref firstForfeits,
                ref secondForfeits,
                ref firstIllegalMoves,
                ref secondIllegalMoves,
                ref firstTimeouts,
                ref secondTimeouts,
                ref firstProtocols,
                ref secondProtocols,
                ref firstCrashErrors,
                ref secondCrashErrors);
        }

        var count = records.Count;
        var first = new BotAggregate(
            firstWins,
            draws,
            secondWins,
            Score(firstWins, draws, count),
            firstForfeits,
            firstIllegalMoves,
            firstTimeouts,
            firstProtocols,
            firstCrashErrors,
            TimingSummary.FromSamples(firstTimes));
        var second = new BotAggregate(
            secondWins,
            draws,
            firstWins,
            Score(secondWins, draws, count),
            secondForfeits,
            secondIllegalMoves,
            secondTimeouts,
            secondProtocols,
            secondCrashErrors,
            TimingSummary.FromSamples(secondTimes));
        return new ReportAggregate(
            count,
            firstForfeits + secondForfeits,
            firstIllegalMoves + secondIllegalMoves,
            firstTimeouts + secondTimeouts,
            firstProtocols + secondProtocols,
            firstCrashErrors + secondCrashErrors,
            first,
            second);
    }

    public static async Task WriteAtomicAsync(
        string path,
        HarnessJsonReport report,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(report);

        var destination = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("JSON output path has no parent directory.", nameof(path));
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream, report, JsonOptions, cancellationToken);
                await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static ReportGame ToReportGame(BenchmarkGameRecord game)
    {
        var firstTiming = game.FirstPlayedX ? game.Report.XTiming : game.Report.OTiming;
        var secondTiming = game.FirstPlayedX ? game.Report.OTiming : game.Report.XTiming;
        var failure = game.Report.Failure;
        var failureBot = failure is null
            ? null
            : failure.Mark == (game.FirstPlayedX
                ? UttArena.Contracts.PlayerMark.X
                : UttArena.Contracts.PlayerMark.O)
                ? "first"
                : "second";
        var category = failure is null ? null : FailureCategory(failure.Kind);
        var winner = game.Report.Outcome switch
        {
            GameOutcome.Draw => null,
            GameOutcome.XWin => game.FirstPlayedX ? "first" : "second",
            _ => game.FirstPlayedX ? "second" : "first",
        };

        return new ReportGame(
            game.Index,
            game.MatchId,
            new ReportOpening(game.OpeningId, game.OpeningMoves),
            new ReportColorAssignment(
                game.FirstPlayedX ? "first" : "second",
                game.FirstPlayedX ? "second" : "first"),
            game.GameSeed,
            OutcomeName(game.Report.Outcome),
            winner,
            game.Report.Plies,
            new ReportTiming(
                TimingSummary.FromSamples(firstTiming.MoveTimesMs),
                TimingSummary.FromSamples(secondTiming.MoveTimesMs)),
            game.Report.Forfeited,
            failure?.Kind == TurnFailureKind.IllegalMove,
            failure?.Kind == TurnFailureKind.Timeout,
            category,
            failure is null
                ? null
                : new ReportFailure(
                    failureBot!,
                    failure.Mark == UttArena.Contracts.PlayerMark.X ? "x" : "o",
                    failure.Ply,
                    category!,
                    failure.Reason));
    }

    private static void IncrementFailure(
        TurnFailureKind kind,
        bool firstFailed,
        ref int firstForfeits,
        ref int secondForfeits,
        ref int firstIllegalMoves,
        ref int secondIllegalMoves,
        ref int firstTimeouts,
        ref int secondTimeouts,
        ref int firstProtocols,
        ref int secondProtocols,
        ref int firstCrashErrors,
        ref int secondCrashErrors)
    {
        if (firstFailed)
        {
            firstForfeits++;
            IncrementKind(
                kind, ref firstIllegalMoves, ref firstTimeouts, ref firstProtocols, ref firstCrashErrors);
        }
        else
        {
            secondForfeits++;
            IncrementKind(
                kind, ref secondIllegalMoves, ref secondTimeouts, ref secondProtocols, ref secondCrashErrors);
        }
    }

    private static void IncrementKind(
        TurnFailureKind kind,
        ref int illegalMoves,
        ref int timeouts,
        ref int protocols,
        ref int crashErrors)
    {
        switch (kind)
        {
            case TurnFailureKind.IllegalMove:
                illegalMoves++;
                break;
            case TurnFailureKind.Timeout:
                timeouts++;
                break;
            case TurnFailureKind.Protocol:
                protocols++;
                break;
            case TurnFailureKind.Crash:
            case TurnFailureKind.Error:
                crashErrors++;
                break;
        }
    }

    private static double Score(int wins, int draws, int games) =>
        games == 0 ? 0 : Math.Round((wins + (draws / 2d)) / games, 6);

    private static string VariantName(UttArena.GameEngine.GameVariant variant) => variant switch
    {
        UttArena.GameEngine.GameVariant.Wildcard => "wildcard",
        UttArena.GameEngine.GameVariant.DoubleMove => "doubleMove",
        _ => "standard",
    };

    private static string OutcomeName(GameOutcome outcome) => outcome switch
    {
        GameOutcome.XWin => "x_win",
        GameOutcome.OWin => "o_win",
        _ => "draw",
    };

    private static string FailureCategory(TurnFailureKind kind) => kind switch
    {
        TurnFailureKind.Timeout => "timeout",
        TurnFailureKind.IllegalMove => "illegal_move",
        TurnFailureKind.Protocol => "protocol",
        TurnFailureKind.Crash => "crash",
        _ => "error",
    };
}
