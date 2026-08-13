using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using UttArena.BotSdk.CSharp;
using UttArena.Contracts;
using UttArena.GameEngine;
using UttArena.PolicyValueBot;

var weightsPath = OptionValue(args, "--weights")
    ?? Environment.GetEnvironmentVariable("UTTARENA_WEIGHTS_PATH")
    ?? Path.Combine(AppContext.BaseDirectory, "weights.bin");

try
{
    var weights = WeightFile.Load(weightsPath);
    var network = new PolicyValueNetwork(weights);

    var networkEvalIndex = Array.IndexOf(args, "--network-eval");
    if (networkEvalIndex >= 0)
    {
        var inputPath = networkEvalIndex + 1 < args.Length &&
            !args[networkEvalIndex + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[networkEvalIndex + 1]
                : null;
        return await NetworkDiagnostic.RunAsync(network, inputPath);
    }

    if (args.Contains("--self-test", StringComparer.Ordinal))
    {
        return SelfTest.Run(network, weights) ? 0 : 1;
    }

    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.Cancel();
    };

    var simulations = ReadSimulationLimit();
    await BotConsoleHost.RunAsync(
        new PolicyValueArenaBot(network, simulations),
        cancellationToken: shutdown.Token);
    return 0;
}
catch (OperationCanceledException)
{
    return 130;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Policy/value bot startup failed: {exception.Message}");
    return 1;
}

static string? OptionValue(string[] arguments, string name)
{
    var index = Array.IndexOf(arguments, name);
    if (index < 0)
    {
        return null;
    }

    if (index + 1 >= arguments.Length)
    {
        throw new ArgumentException($"{name} requires a value.");
    }

    return arguments[index + 1];
}

static int ReadSimulationLimit()
{
    const int defaultSimulations = 256;
    var configured = Environment.GetEnvironmentVariable("UTTARENA_PUCT_SIMULATIONS");
    if (string.IsNullOrWhiteSpace(configured))
    {
        return defaultSimulations;
    }

    if (!int.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
        value is < 1 or > 4096)
    {
        throw new InvalidOperationException(
            "UTTARENA_PUCT_SIMULATIONS must be an integer from 1 through 4096.");
    }

    return value;
}

internal sealed class PolicyValueArenaBot : IBot
{
    private const double BudgetFraction = 0.6;
    private static readonly TimeSpan TinyBudget = TimeSpan.FromMilliseconds(2);
    private readonly PuctSearch _search;

    public PolicyValueArenaBot(PolicyValueNetwork network, int simulations)
    {
        _search = new PuctSearch(network, simulations);
    }

    public ValueTask<BotDecision> GetMoveAsync(
        MoveRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var fallback = request.LegalMoves[0];
        var budget = MoveBudget(request.Timing);
        BoardPosition move;
        if (budget <= TinyBudget)
        {
            move = fallback;
        }
        else
        {
            var state = ProtocolBridge.ToGameState(request);
            var result = _search.Search(state, budget, cancellationToken);
            if (result.Action is not int action)
            {
                move = fallback;
            }
            else
            {
                var selected = ProtocolBridge.ToPosition(new Move(action / 9, action % 9));
                move = request.LegalMoves.Contains(selected) ? selected : fallback;
            }
        }

        return ValueTask.FromResult(new BotDecision(move, PickBanter(request)));
    }

    [Banter]
    private static string? PickBanter(MoveRequest request)
    {
        var situations = BanterSituations.Analyze(request);
        if (situations.HasFlag(BanterSituation.BotCanTakeLocalBoard))
        {
            return "Policy agrees — claiming this board.";
        }

        if (situations.HasFlag(BanterSituation.OpponentJustWonLocalBoard))
        {
            return "You took a board. Value head still calm.";
        }

        if (situations.HasFlag(BanterSituation.OpponentHasImmediateLocalThreat))
        {
            return "Threat spotted. Searching the save.";
        }

        if (situations.HasFlag(BanterSituation.FreeMove))
        {
            return "Free board. Sampling the prior.";
        }

        if (situations.HasFlag(BanterSituation.Opening))
        {
            return "Opening book: pure self-play vibes.";
        }

        if (situations.HasFlag(BanterSituation.Endgame))
        {
            return "Late game — every visit counts.";
        }

        return null;
    }

    private static TimeSpan MoveBudget(MoveTiming timing)
    {
        var deadlineRemaining = timing.DeadlineUtc - DateTimeOffset.UtcNow;
        var protocolTimeout = TimeSpan.FromMilliseconds(timing.MoveTimeoutMs);
        var available = deadlineRemaining < protocolTimeout
            ? deadlineRemaining
            : protocolTimeout;
        return available <= TimeSpan.Zero
            ? TimeSpan.Zero
            : TimeSpan.FromTicks((long)(available.Ticks * BudgetFraction));
    }
}

internal static class NetworkDiagnostic
{
    private static readonly JsonSerializerOptions OutputOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static async Task<int> RunAsync(PolicyValueNetwork network, string? inputPath)
    {
        var json = inputPath is null
            ? await Console.In.ReadToEndAsync()
            : await File.ReadAllTextAsync(inputPath);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("features", out var featureElement))
        {
            throw new InvalidDataException("Network input must be an object with 'features'.");
        }

        var features = new List<float>(PolicyValueNetwork.FeatureCount);
        FlattenNumbers(featureElement, features);
        if (features.Count != PolicyValueNetwork.FeatureCount)
        {
            throw new InvalidDataException("Features must contain exactly 567 numeric values.");
        }

        bool[]? mask = null;
        if (root.TryGetProperty("legalMask", out var maskElement) &&
            maskElement.ValueKind != JsonValueKind.Null)
        {
            var values = new List<bool>(PolicyValueNetwork.ActionCount);
            FlattenBooleans(maskElement, values);
            if (values.Count != PolicyValueNetwork.ActionCount)
            {
                throw new InvalidDataException("legalMask must contain exactly 81 booleans.");
            }

            mask = values.ToArray();
        }

        var output = network.Evaluate(features.ToArray(), mask);
        Console.Out.WriteLine(JsonSerializer.Serialize(
            new DiagnosticOutput(output.Logits, output.Value),
            OutputOptions));
        return 0;
    }

    private static void FlattenNumbers(JsonElement element, List<float> destination)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                FlattenNumbers(child, destination);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Number ||
            !element.TryGetSingle(out var value) ||
            !float.IsFinite(value))
        {
            throw new InvalidDataException("Features must contain only finite FP32 numbers.");
        }

        destination.Add(value);
    }

    private static void FlattenBooleans(JsonElement element, List<bool> destination)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                FlattenBooleans(child, destination);
            }

            return;
        }

        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException("legalMask must contain only booleans.");
        }

        destination.Add(element.GetBoolean());
    }

    private sealed record DiagnosticOutput(float[] Logits, float Value);
}

internal static class SelfTest
{
    public static bool Run(PolicyValueNetwork network, WeightSet weights)
    {
        var state = GameState.Initial();
        if (!FeatureEncodingIsValid())
        {
            Console.Error.WriteLine("FAIL: current-player-relative feature encoding is incorrect.");
            return false;
        }

        var features = FeatureEncoder.Encode(state);
        var output = network.Evaluate(features, FeatureEncoder.LegalMask(state));
        if (output.Logits.Length != 81 ||
            output.Logits.Any(float.IsNaN) ||
            !float.IsFinite(output.Value) ||
            output.Value is < -1 or > 1)
        {
            Console.Error.WriteLine("FAIL: network inference returned invalid output.");
            return false;
        }

        var result = new PuctSearch(network, maximumSimulations: 16)
            .Search(state, TimeSpan.FromMilliseconds(500), CancellationToken.None);
        if (result.Action is not int action ||
            !UltimateTicTacToe.IsLegalMove(state, new Move(action / 9, action % 9)))
        {
            Console.Error.WriteLine("FAIL: search did not return a legal opening move.");
            return false;
        }

        Console.Error.WriteLine(
            $"PASS: schema {WeightFile.SchemaVersion}, {weights.WeightBytes} weight bytes, " +
            $"{result.Simulations} simulations, legal action {action}.");
        return true;
    }

    private static bool FeatureEncodingIsValid()
    {
        var routed = UltimateTicTacToe.ApplyMove(GameState.Initial(), new Move(4, 4));
        var routedFeatures = FeatureEncoder.Encode(routed);
        var boardFourTopLeft = (3 * 9) + 3;
        if (routedFeatures[81 + 40] != 1 ||
            routedFeatures[40] != 0 ||
            routedFeatures[(5 * 81) + boardFourTopLeft] != 1 ||
            routedFeatures[(5 * 81) + 40] != 0 ||
            routedFeatures[(6 * 81) + 40] != 1)
        {
            return false;
        }

        var cells = new Cell[81];
        cells[0] = Cell.X;
        cells[1] = Cell.X;
        cells[2] = Cell.X;
        var wonBoard = GameState.Create(cells, Cell.O);
        var wonFeatures = FeatureEncoder.Encode(wonBoard);
        return wonFeatures[(3 * 81) + 0] == 1 &&
            wonFeatures[(3 * 81) + 1] == 1 &&
            wonFeatures[(3 * 81) + 2] == 1 &&
            wonFeatures[(3 * 81) + 9] == 1 &&
            wonFeatures[(3 * 81) + 10] == 1 &&
            wonFeatures[(3 * 81) + 11] == 1 &&
            wonFeatures[(3 * 81) + 18] == 1 &&
            wonFeatures[(3 * 81) + 19] == 1 &&
            wonFeatures[(3 * 81) + 20] == 1;
    }
}
