using System.Reflection;
using UttArena.BotHarness;
using UttArena.Contracts;
using UttArena.GameEngine;

namespace UttArena.BotHarness.Tests;

public sealed class ConformanceFixtureTests
{
    [Fact]
    public void EveryFixtureIsAValidProtocolRequest()
    {
        var fixtures = Conformance.Fixtures();

        Assert.NotEmpty(fixtures);
        foreach (var fixture in fixtures)
        {
            var validation = ProtocolValidation.Validate(fixture.Request);
            Assert.True(
                validation.IsValid,
                $"{fixture.Name}: {string.Join("; ", validation.Errors)}");
            Assert.NotEmpty(fixture.Request.LegalMoves);
        }
    }

    [Fact]
    public void FixturesCoverTheForcedBoardAndFreeChoiceRules()
    {
        var fixtures = Conformance.Fixtures();

        Assert.Contains(fixtures, fixture => fixture.Request.Board.ActiveLocalBoard is not null);
        Assert.Contains(
            fixtures,
            fixture => fixture.Request.Board.ActiveLocalBoard is null &&
                fixture.Request.Board.MoveNumber > 0);
    }

    [Fact]
    public void FixturesExposeWildcardAndDoubleMoveStates()
    {
        var fixtures = Conformance.Fixtures();
        var wildcard = fixtures.Single(fixture =>
            fixture.Name == "wildcard-special-available").Request;
        var doubleMove = fixtures.Single(fixture =>
            fixture.Name == "double-move-special-available").Request;
        var pending = fixtures.Single(fixture =>
            fixture.Name == "double-move-pending").Request;

        Assert.Equal(ProtocolGameVariant.Wildcard, wildcard.Variant);
        Assert.True(wildcard.SpecialAvailability!.X);
        Assert.NotEmpty(wildcard.LegalSpecialMoves!);
        Assert.Equal(ProtocolGameVariant.DoubleMove, doubleMove.Variant);
        Assert.NotEmpty(doubleMove.LegalSpecialMoves!);
        Assert.True(pending.DoubleMovePending);
        Assert.Null(pending.LegalSpecialMoves);
        Assert.False(pending.SpecialAvailability!.X);
    }

    [Fact]
    public void FixturesAreDeterministic()
    {
        var first = Conformance.Fixtures();
        var second = Conformance.Fixtures();

        Assert.Equal(first.Count, second.Count);
        for (var index = 0; index < first.Count; index++)
        {
            Assert.Equal(first[index].Request.Board.MoveNumber, second[index].Request.Board.MoveNumber);
            Assert.Equal(
                first[index].Request.LegalMoves.Count,
                second[index].Request.LegalMoves.Count);
        }
    }
}

public sealed class SampleBotConformanceTests
{
    // Generous compared with the arena's budget: a cold-started child process on a
    // busy CI machine is slower than the same bot inside a warm container.
    private const int BudgetMs = 3_000;

    [Theory]
    [InlineData("bot-template-csharp")]
    [InlineData("bot-random-csharp")]
    [InlineData("bot-heuristic-csharp")]
    [InlineData("bot-minimax-csharp")]
    [InlineData("bot-mcts-csharp")]
    [InlineData("bot-policy-value-csharp")]
    public async Task SampleBotAnswersEveryFixtureLegallyWithinBudget(string sample)
    {
        var command = SampleBots.CommandFor(sample);

        var results = await Conformance.RunAsync(command, BudgetMs, CancellationToken.None);

        var failures = results.Where(result => !result.Passed).ToArray();
        Assert.True(
            failures.Length == 0,
            string.Join(
                Environment.NewLine,
                failures.Select(failure => $"{sample}/{failure.Name}: {failure.Detail}")));
    }

    // Proves the full harness loop: two real child processes play a legal game to a
    // terminal position with no forfeits, and the searching bot beats the random one.
    [Fact]
    public async Task StrongerSampleBeatsRandomWithBothColoursPlayed()
    {
        var minimax = SampleBots.CommandFor("bot-minimax-csharp");
        var random = SampleBots.CommandFor("bot-random-csharp");
        var settings = new GameSettings(MoveTimeoutMs: 500, StrictTimeouts: false);
        var searcherWins = 0;

        foreach (var searcherPlaysX in new[] { true, false })
        {
            await using var x = BotProcess.Start("x", searcherPlaysX ? minimax : random);
            await using var o = BotProcess.Start("o", searcherPlaysX ? random : minimax);

            var report = await GameRunner.PlayAsync(x, o, settings, CancellationToken.None);

            Assert.False(report.Forfeited, report.Failure?.Reason);
            Assert.True(report.Plies > 0);
            var won = report.Outcome == (searcherPlaysX ? GameOutcome.XWin : GameOutcome.OWin);
            if (won)
            {
                searcherWins++;
            }
        }

        Assert.True(searcherWins > 0, "The searching bot failed to beat random from either side.");
    }
}

internal static class SampleBots
{
    public static string CommandFor(string sample)
    {
        var path = Path.Combine(RepositoryRoot(), "samples", sample, "bin", Configuration(), "net8.0", $"{sample}.dll");
        Assert.True(
            File.Exists(path),
            $"{path} is missing. Build the solution so the sample bots exist before running these tests.");
        return $"dotnet \"{path}\"";
    }

    private static string Configuration() =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";

    // tests/UttArena.BotHarness.Tests/bin/<config>/net8.0 -> repository root
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
