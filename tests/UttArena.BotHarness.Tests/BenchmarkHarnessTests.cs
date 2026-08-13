using System.Globalization;
using System.Text.Json;
using UttArena.BotHarness;
using UttArena.Contracts;
using UttArena.GameEngine;

namespace UttArena.BotHarness.Tests;

public sealed class HarnessOptionTests
{
    [Fact]
    public void ExistingSeriesOptionsKeepTheirDefaults()
    {
        var options = HarnessOptions.Parse(["--x", "first", "--o", "second"]);

        Assert.NotNull(options);
        Assert.Equal(2, options.Games);
        Assert.Equal(1_000, options.MoveTimeoutMs);
        Assert.True(options.StrictTimeouts);
        Assert.Equal(GameVariant.Standard, options.Variant);
        Assert.Null(options.JsonOutputPath);
        Assert.Null(options.OpeningsPath);
        Assert.Equal(0UL, options.Seed);
    }

    [Fact]
    public void BenchmarkOptionsParse()
    {
        var options = HarnessOptions.Parse(
        [
            "--x", "first",
            "--o", "second",
            "--json-out", "report.json",
            "--openings", "openings.json",
            "--seed", ulong.MaxValue.ToString(CultureInfo.InvariantCulture),
            "--move-timeout-ms", "250",
        ]);

        Assert.NotNull(options);
        Assert.Equal("report.json", options.JsonOutputPath);
        Assert.Equal("openings.json", options.OpeningsPath);
        Assert.Equal(ulong.MaxValue, options.Seed);
        Assert.Equal(250, options.MoveTimeoutMs);
    }

    [Fact]
    public void OpeningsRejectAmbiguousGamesAndNonStandardVariant()
    {
        Assert.Null(HarnessOptions.Parse(
            ["--x", "first", "--o", "second", "--openings", "suite.json", "--games", "2"]));
        Assert.Null(HarnessOptions.Parse(
            ["--x", "first", "--o", "second", "--openings", "suite.json", "--variant", "wildcard"]));
    }
}

public sealed class OpeningSuiteTests
{
    [Fact]
    public void FixedSuiteHasOneHundredUniqueLegalNonTerminalPrefixes()
    {
        var path = Path.Combine(
            RepositoryRoot(), "training", "uttarena-ml", "config", "openings-standard-v1.json");

        var suite = OpeningSuite.Load(path);

        Assert.Equal(100, suite.Openings.Count);
        Assert.Contains(suite.Openings, opening => opening.Moves.Count == 0);
        Assert.Equal(100, suite.Openings.Select(opening => string.Join(",", opening.Moves)).Distinct().Count());
    }

    [Fact]
    public void ValidationRejectsDuplicateIllegalAndTerminalPrefixes()
    {
        var rules = StandardRules();
        var duplicate = Document(
            rules,
            new OpeningDefinition("empty", []),
            new OpeningDefinition("again", []));
        Assert.Throws<OpeningSuiteException>(() => OpeningSuite.Validate(duplicate));

        var illegal = Document(
            rules,
            new OpeningDefinition("empty", []),
            new OpeningDefinition("illegal", [0, 9]));
        Assert.Throws<OpeningSuiteException>(() => OpeningSuite.Validate(illegal));

        var terminalMoves = TerminalPrefix();
        var terminal = Document(
            rules,
            new OpeningDefinition("empty", []),
            new OpeningDefinition("terminal", terminalMoves));
        Assert.Throws<OpeningSuiteException>(() => OpeningSuite.Validate(terminal));
    }

    private static OpeningRules StandardRules() =>
        new(
            OpeningSuite.RulesId,
            OpeningSuite.RulesVersion,
            OpeningSuite.Variant,
            OpeningSuite.ActionEncoding);

    private static OpeningSuiteDocument Document(
        OpeningRules rules,
        params OpeningDefinition[] openings) =>
        new(null, OpeningSuite.SchemaName, OpeningSuite.SchemaVersion, rules, openings);

    private static List<int> TerminalPrefix()
    {
        var state = GameState.Initial();
        var actions = new List<int>();
        while (!state.IsTerminal)
        {
            var move = UltimateTicTacToe.GetLegalMoves(state)[0];
            actions.Add((move.Board * 9) + move.Cell);
            state = UltimateTicTacToe.ApplyMove(state, move);
        }
        return actions;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UttArena.slnx")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        return directory!.FullName;
    }
}

public sealed class GameSetupTests
{
    [Fact]
    public void OpeningMovesUseTheConfiguredSeedAndPopulateHistory()
    {
        var move = new Move(4, 4);
        var initial = GameState.Initial(GameVariant.Chaos);
        var seedZero = UltimateTicTacToe.ApplyMove(initial, move, GameVariant.Chaos, 0);
        var selectedSeed = Enumerable.Range(1, 100)
            .Select(value => (ulong)value)
            .First(seed => UltimateTicTacToe.ApplyMove(
                initial, move, GameVariant.Chaos, seed).ForcedBoard != seedZero.ForcedBoard);
        var expected = UltimateTicTacToe.ApplyMove(
            initial, move, GameVariant.Chaos, selectedSeed);

        var prepared = GameSetup.Prepare(new GameSettings(
            Variant: GameVariant.Chaos,
            Seed: selectedSeed,
            OpeningActions: [40]));

        Assert.Equal(expected.ForcedBoard, prepared.State.ForcedBoard);
        Assert.Equal(1, prepared.State.Ply);
        var history = Assert.Single(prepared.History);
        Assert.Equal(1, history.Ply);
        Assert.Equal(PlayerMark.X, history.Mark);
        Assert.Equal(ProtocolBridge.ToPosition(move), history.Position);
    }

    [Fact]
    public void InitialStateRequiresMatchingHistory()
    {
        var state = UltimateTicTacToe.ApplyMove(GameState.Initial(), new Move(4, 4));

        Assert.Throws<ArgumentException>(() => GameSetup.Prepare(
            new GameSettings(InitialState: state)));

        var history = new[]
        {
            new MoveRecord(1, PlayerMark.X, ProtocolBridge.ToPosition(new Move(4, 4))),
        };
        var prepared = GameSetup.Prepare(
            new GameSettings(InitialState: state, InitialHistory: history));
        Assert.Same(state, prepared.State);
        Assert.Equal(history, prepared.History);
    }
}

public sealed class HarnessReportTests
{
    [Fact]
    public async Task ReportSerializesVersionedJsonAtomicallyWithSwappedColors()
    {
        var options = Assert.IsType<HarnessOptions>(HarnessOptions.Parse(
            ["--x", "candidate", "--o", "champion", "--json-out", "unused.json"]));
        var records = new[]
        {
            Record(1, true, GameOutcome.XWin, [1, 3], [2]),
            Record(2, false, GameOutcome.OWin, [5], [4, 9]),
        };
        var report = HarnessReport.Create(options, records, openingCount: null);
        var path = Path.Combine(
            Path.GetTempPath(), $"uttarena-harness-{Guid.NewGuid():N}", "report.json");

        try
        {
            await HarnessReport.WriteAtomicAsync(path, report, CancellationToken.None);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal(HarnessReport.SchemaName, json.RootElement.GetProperty("schema").GetString());
            Assert.Equal(HarnessReport.SchemaVersion, json.RootElement.GetProperty("version").GetInt32());
            var games = json.RootElement.GetProperty("games");
            Assert.Equal("first", games[0].GetProperty("colorAssignment").GetProperty("x").GetString());
            Assert.Equal("second", games[1].GetProperty("colorAssignment").GetProperty("x").GetString());
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(path)))
            {
                Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
            }
        }
    }

    [Fact]
    public void AggregateComputesScoresFailuresAndNearestRankTiming()
    {
        var records = new[]
        {
            Record(1, true, GameOutcome.XWin, [1, 10], [2]),
            Record(2, false, GameOutcome.XWin, [20], [3, 30]),
            Record(3, true, GameOutcome.Draw, [40], [4]),
            Record(
                4,
                false,
                GameOutcome.XWin,
                [50],
                [5],
                new TurnFailure("first", PlayerMark.O, 7, TurnFailureKind.IllegalMove, "bad")),
        };

        var aggregate = HarnessReport.Aggregate(records);

        Assert.Equal(4, aggregate.Games);
        Assert.Equal(1, aggregate.First.Wins);
        Assert.Equal(1, aggregate.First.Draws);
        Assert.Equal(2, aggregate.First.Losses);
        Assert.Equal(0.375, aggregate.First.Score);
        Assert.Equal(1, aggregate.First.Forfeits);
        Assert.Equal(1, aggregate.First.IllegalMoves);
        Assert.Equal(6, aggregate.First.Timing.Moves);
        Assert.Equal(89, aggregate.First.Timing.TotalMs);
        Assert.Equal(5, aggregate.First.Timing.P50Ms);
        Assert.Equal(40, aggregate.First.Timing.P95Ms);
        Assert.Equal(1, aggregate.Forfeits);
    }

    private static BenchmarkGameRecord Record(
        int index,
        bool firstPlayedX,
        GameOutcome outcome,
        IReadOnlyList<long> xTimes,
        IReadOnlyList<long> oTimes,
        TurnFailure? failure = null) =>
        new(
            index,
            $"match-{index}",
            "empty",
            [],
            firstPlayedX,
            (ulong)index,
            new GameReport(outcome, 10, 50, failure)
            {
                XTiming = new BotMoveTiming(xTimes),
                OTiming = new BotMoveTiming(oTimes),
                Seed = (ulong)index,
            });
}
