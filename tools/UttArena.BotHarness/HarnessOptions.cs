using UttArena.GameEngine;

namespace UttArena.BotHarness;

public sealed record HarnessOptions(
    string XCommand,
    string OCommand,
    string? Conformance,
    int Games,
    int MoveTimeoutMs,
    bool StrictTimeouts,
    bool Verbose,
    GameVariant Variant,
    string? JsonOutputPath,
    string? OpeningsPath,
    ulong Seed,
    bool GamesSpecified)
{
    public static HarnessOptions? Parse(string[] args)
    {
        string? x = null;
        string? o = null;
        string? conformance = null;
        string? jsonOutputPath = null;
        string? openingsPath = null;
        var games = 2;
        var gamesSpecified = false;
        var timeout = 1_000;
        var strict = true;
        var verbose = false;
        var variant = GameVariant.Standard;
        ulong seed = 0;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--x" when index + 1 < args.Length:
                    x = args[++index];
                    break;
                case "--o" when index + 1 < args.Length:
                    o = args[++index];
                    break;
                case "--conformance" when index + 1 < args.Length:
                    conformance = args[++index];
                    break;
                case "--games" when index + 1 < args.Length:
                    if (!int.TryParse(args[++index], out games) || games < 1)
                    {
                        return null;
                    }
                    gamesSpecified = true;
                    break;
                case "--move-timeout-ms" when index + 1 < args.Length:
                    if (!int.TryParse(args[++index], out timeout) || timeout < 1)
                    {
                        return null;
                    }
                    break;
                case "--variant" when index + 1 < args.Length:
                    if (!Enum.TryParse<GameVariant>(args[++index], ignoreCase: true, out variant) ||
                        variant is GameVariant.DraftOpening or GameVariant.Relay or GameVariant.LastStand)
                    {
                        return null;
                    }
                    break;
                case "--json-out" when index + 1 < args.Length:
                    jsonOutputPath = args[++index];
                    break;
                case "--openings" when index + 1 < args.Length:
                    openingsPath = args[++index];
                    break;
                case "--seed" when index + 1 < args.Length:
                    if (!ulong.TryParse(args[++index], out seed))
                    {
                        return null;
                    }
                    break;
                case "--lenient-timeouts":
                    strict = false;
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                default:
                    return null;
            }
        }

        if (conformance is not null)
        {
            if (x is not null || o is not null || jsonOutputPath is not null || openingsPath is not null)
            {
                return null;
            }

            return new HarnessOptions(
                "", "", conformance, games, timeout, strict, verbose, variant,
                null, null, seed, gamesSpecified);
        }

        if (x is null || o is null ||
            (openingsPath is not null && (gamesSpecified || variant != GameVariant.Standard)))
        {
            return null;
        }

        return new HarnessOptions(
            x, o, null, games, timeout, strict, verbose, variant,
            jsonOutputPath, openingsPath, seed, gamesSpecified);
    }

    public static void PrintUsage()
    {
        Console.Error.WriteLine(
            """
            Runs UTT Arena bots against each other locally over the real protocol.

            Play a series:
              --x <command>              command that starts the first bot
              --o <command>              command that starts the second bot
              --games <n>                games to play, sides swapped each game (default 2)
              --openings <path>          Standard v1 opening suite; each opening is played
                                         exactly twice with colors swapped (cannot use --games)
              --json-out <path>          atomically write a versioned JSON benchmark report
              --seed <ulong>             base game seed (default 0)

            Check one bot against protocol fixtures:
              --conformance <command>    command that starts the bot under test

            Shared options:
              --move-timeout-ms <n>      per-move budget in milliseconds (default 1000)
              --variant <name>           standard, wildcard, doubleMove, chaos, anarchy,
                                         suddenDeath, territory, misere, centerControl,
                                         or lockedArena (default standard)
              --lenient-timeouts         report slow moves instead of forfeiting them
              --verbose                  echo each bot's stderr

            Example:
              dotnet run --project tools/UttArena.BotHarness -- \
                --x "dotnet run -c Release --project samples/bot-minimax-csharp" \
                --o "dotnet run -c Release --project samples/bot-random-csharp" \
                --games 10
            """);
    }
}
