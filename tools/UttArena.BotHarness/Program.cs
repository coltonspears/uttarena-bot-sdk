using UttArena.BotHarness;
using UttArena.Contracts;
using UttArena.GameEngine;

// Local bot-vs-bot harness. Runs real child processes over the real protocol
// against the real rules engine, with no Docker, no database, and no API, so an
// author can iterate in seconds instead of doing upload-and-build round trips.

var options = HarnessOptions.Parse(args);
if (options is null)
{
    HarnessOptions.PrintUsage();
    return 1;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    return options.Conformance is { } candidate
        ? await RunConformanceAsync(candidate, options, cancellation.Token)
        : await RunSeriesAsync(options, cancellation.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (OpeningSuiteException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static async Task<int> RunConformanceAsync(
    string command,
    HarnessOptions options,
    CancellationToken cancellationToken)
{
    Console.WriteLine($"Protocol conformance: {command}");
    Console.WriteLine();

    var results = await Conformance.RunAsync(command, options.MoveTimeoutMs, cancellationToken);
    foreach (var result in results)
    {
        Console.WriteLine(result.Passed
            ? $"  PASS  {result.Name,-34} {result.ElapsedMs,5} ms"
            : $"  FAIL  {result.Name,-34} {result.ElapsedMs,5} ms  {result.Detail}");
    }

    var failures = results.Count(result => !result.Passed);
    Console.WriteLine();
    Console.WriteLine($"{results.Count - failures}/{results.Count} fixtures passed.");
    return failures == 0 ? 0 : 1;
}

static async Task<int> RunSeriesAsync(HarnessOptions options, CancellationToken cancellationToken)
{
    var tally = new Tally(options.XCommand, options.OCommand);
    var records = new List<BenchmarkGameRecord>();
    var openingSuite = options.OpeningsPath is null
        ? null
        : OpeningSuite.Load(options.OpeningsPath);
    var schedule = BuildSchedule(options, openingSuite);

    Console.WriteLine($"{options.XCommand}");
    Console.WriteLine($"  vs {options.OCommand}");
    if (openingSuite is null)
    {
        Console.WriteLine(
            $"{options.Games} {options.Variant} games, {options.MoveTimeoutMs} ms per move, sides swapped each game.");
    }
    else
    {
        Console.WriteLine(
            $"{openingSuite.Openings.Count} paired Standard openings ({schedule.Count} games), " +
            $"{options.MoveTimeoutMs} ms per move.");
    }
    Console.WriteLine();

    foreach (var game in schedule)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var xCommand = game.FirstPlayedX ? options.XCommand : options.OCommand;
        var oCommand = game.FirstPlayedX ? options.OCommand : options.XCommand;
        var xName = game.FirstPlayedX ? "first" : "second";
        var oName = game.FirstPlayedX ? "second" : "first";
        var settings = new GameSettings(
            options.MoveTimeoutMs,
            options.StrictTimeouts,
            options.Variant,
            game.GameSeed,
            game.OpeningMoves,
            MatchId: game.MatchId);

        BotProcess? x = null;
        BotProcess? o = null;
        GameReport report;
        try
        {
            try
            {
                x = BotProcess.Start(xName, xCommand);
            }
            catch (Exception exception)
            {
                report = StartupForfeit(settings, xName, PlayerMark.X, exception);
                goto GameComplete;
            }

            try
            {
                o = BotProcess.Start(oName, oCommand);
            }
            catch (Exception exception)
            {
                report = StartupForfeit(settings, oName, PlayerMark.O, exception);
                goto GameComplete;
            }

            report = await GameRunner.PlayAsync(x, o, settings, cancellationToken);

        GameComplete:
            tally.Record(report, game.FirstPlayedX);
            records.Add(new BenchmarkGameRecord(
                game.Index,
                game.MatchId,
                game.OpeningId,
                game.OpeningMoves,
                game.FirstPlayedX,
                game.GameSeed,
                report));

            var winner = report.Outcome switch
            {
                GameOutcome.Draw => "draw",
                GameOutcome.XWin => game.FirstPlayedX ? "first" : "second",
                _ => game.FirstPlayedX ? "second" : "first"
            };
            Console.WriteLine(
                $"  game {game.Index,3}  {winner,-7} {report.Plies,3} plies  peak {report.LongestThinkMs,5} ms" +
                (report.Failure is null ? "" : $"  forfeit: {report.Failure.Reason}"));

            if (options.Verbose)
            {
                if (x is not null)
                {
                    WriteDiagnostics(x);
                }
                if (o is not null)
                {
                    WriteDiagnostics(o);
                }
            }
        }
        finally
        {
            if (o is not null)
            {
                await o.DisposeAsync();
            }
            if (x is not null)
            {
                await x.DisposeAsync();
            }
        }
    }

    Console.WriteLine();
    tally.Print();
    if (options.JsonOutputPath is not null)
    {
        var jsonReport = HarnessReport.Create(options, records, openingSuite?.Openings.Count);
        await HarnessReport.WriteAtomicAsync(
            options.JsonOutputPath, jsonReport, cancellationToken);
    }
    return tally.Forfeits == 0 ? 0 : 1;
}

static IReadOnlyList<ScheduledGame> BuildSchedule(
    HarnessOptions options,
    OpeningSuiteDocument? openingSuite)
{
    var schedule = new List<ScheduledGame>();
    if (openingSuite is null)
    {
        for (var game = 0; game < options.Games; game++)
        {
            var gameSeed = unchecked(options.Seed + (ulong)(game / 2));
            schedule.Add(new ScheduledGame(
                game + 1,
                $"harness-{gameSeed:x16}-{game + 1:D4}",
                "empty",
                Array.Empty<int>(),
                game % 2 == 0,
                gameSeed));
        }
        return schedule;
    }

    for (var openingIndex = 0; openingIndex < openingSuite.Openings.Count; openingIndex++)
    {
        var opening = openingSuite.Openings[openingIndex];
        var gameSeed = unchecked(options.Seed + (ulong)openingIndex);
        foreach (var firstPlayedX in new[] { true, false })
        {
            var index = schedule.Count + 1;
            schedule.Add(new ScheduledGame(
                index,
                $"harness-{gameSeed:x16}-{index:D4}",
                opening.Id,
                opening.Moves.ToArray(),
                firstPlayedX,
                gameSeed));
        }
    }
    return schedule;
}

static GameReport StartupForfeit(
    GameSettings settings,
    string botName,
    PlayerMark mark,
    Exception exception)
{
    var state = GameSetup.Prepare(settings).State;
    return new GameReport(
        mark == PlayerMark.X ? GameOutcome.OWin : GameOutcome.XWin,
        state.Ply,
        0,
        new TurnFailure(botName, mark, state.Ply, TurnFailureKind.Crash, exception.Message))
    {
        Seed = settings.Seed,
        OpeningActions = settings.OpeningActions?.ToArray() ?? Array.Empty<int>(),
    };
}

static void WriteDiagnostics(BotProcess bot)
{
    var diagnostics = bot.Diagnostics;
    if (string.IsNullOrWhiteSpace(diagnostics))
    {
        return;
    }

    foreach (var line in diagnostics.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        Console.WriteLine($"        [{bot.Name}] {line.TrimEnd()}");
    }
}

internal sealed record ScheduledGame(
    int Index,
    string MatchId,
    string OpeningId,
    IReadOnlyList<int> OpeningMoves,
    bool FirstPlayedX,
    ulong GameSeed);

internal sealed class Tally(string firstName, string secondName)
{
    private int _firstWins;
    private int _secondWins;
    private int _draws;
    private long _peakThinkMs;

    public int Forfeits { get; private set; }

    public void Record(GameReport report, bool firstPlayedX)
    {
        _peakThinkMs = Math.Max(_peakThinkMs, report.LongestThinkMs);
        if (report.Forfeited)
        {
            Forfeits++;
        }

        switch (report.Outcome)
        {
            case GameOutcome.Draw:
                _draws++;
                break;
            case GameOutcome.XWin when firstPlayedX:
            case GameOutcome.OWin when !firstPlayedX:
                _firstWins++;
                break;
            default:
                _secondWins++;
                break;
        }
    }

    public void Print()
    {
        var games = _firstWins + _secondWins + _draws;
        Console.WriteLine($"{"",-44}  wins  draws   score");
        Console.WriteLine($"{Shorten(firstName),-44} {_firstWins,5} {_draws,6}  {Score(_firstWins, games):P1}");
        Console.WriteLine($"{Shorten(secondName),-44} {_secondWins,5} {_draws,6}  {Score(_secondWins, games):P1}");
        Console.WriteLine();
        Console.WriteLine($"Peak think time {_peakThinkMs} ms. Forfeits: {Forfeits}.");
    }

    private double Score(int wins, int games) =>
        games == 0 ? 0 : (wins + (_draws / 2d)) / games;

    private static string Shorten(string command) =>
        command.Length <= 44 ? command : string.Concat("...", command.AsSpan(command.Length - 41));
}
