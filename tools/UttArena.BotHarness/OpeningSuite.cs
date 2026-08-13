using System.Text.Json;
using System.Text.Json.Serialization;
using UttArena.GameEngine;

namespace UttArena.BotHarness;

public sealed class OpeningSuiteException(string message) : Exception(message);

public sealed record OpeningRules(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("variant")] string Variant,
    [property: JsonPropertyName("actionEncoding")] string ActionEncoding);

public sealed record OpeningDefinition(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("moves")] IReadOnlyList<int> Moves);

public sealed record OpeningSuiteDocument(
    [property: JsonPropertyName("$schema")] string? JsonSchema,
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("rules")] OpeningRules Rules,
    [property: JsonPropertyName("openings")] IReadOnlyList<OpeningDefinition> Openings);

public static class OpeningSuite
{
    public const string SchemaName = "uttarena.openings";
    public const int SchemaVersion = 1;
    public const string RulesId = "ultimate-tic-tac-toe";
    public const int RulesVersion = 1;
    public const string Variant = "standard";
    public const string ActionEncoding = "board*9+cell";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static OpeningSuiteDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            using var stream = File.OpenRead(path);
            var document = JsonSerializer.Deserialize<OpeningSuiteDocument>(stream, JsonOptions)
                ?? throw new OpeningSuiteException("The opening suite is empty.");
            Validate(document);
            return document;
        }
        catch (OpeningSuiteException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new OpeningSuiteException(
                $"Could not read opening suite '{path}': {exception.Message}");
        }
    }

    public static void Validate(OpeningSuiteDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!string.Equals(document.Schema, SchemaName, StringComparison.Ordinal) ||
            document.Version != SchemaVersion)
        {
            throw new OpeningSuiteException(
                $"Unsupported opening schema '{document.Schema}' version {document.Version}; " +
                $"expected '{SchemaName}' version {SchemaVersion}.");
        }

        if (document.Rules is null ||
            !string.Equals(document.Rules.Id, RulesId, StringComparison.Ordinal) ||
            document.Rules.Version != RulesVersion ||
            !string.Equals(document.Rules.Variant, Variant, StringComparison.Ordinal) ||
            !string.Equals(document.Rules.ActionEncoding, ActionEncoding, StringComparison.Ordinal))
        {
            throw new OpeningSuiteException(
                "Opening rules must be ultimate-tic-tac-toe v1 Standard actions encoded as board*9+cell.");
        }

        if (document.Openings is null || document.Openings.Count == 0)
        {
            throw new OpeningSuiteException("An opening suite must contain at least one opening.");
        }

        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        var hasEmptyBoard = false;
        foreach (var opening in document.Openings)
        {
            if (opening is null || string.IsNullOrWhiteSpace(opening.Id))
            {
                throw new OpeningSuiteException("Every opening must have a non-empty id.");
            }
            if (!identifiers.Add(opening.Id))
            {
                throw new OpeningSuiteException($"Opening id '{opening.Id}' is duplicated.");
            }
            if (opening.Moves is null)
            {
                throw new OpeningSuiteException($"Opening '{opening.Id}' has no moves array.");
            }

            var key = string.Join(",", opening.Moves);
            if (!prefixes.Add(key))
            {
                throw new OpeningSuiteException($"Opening '{opening.Id}' duplicates another move prefix.");
            }
            hasEmptyBoard |= opening.Moves.Count == 0;

            var state = GameState.Initial(GameVariant.Standard);
            for (var index = 0; index < opening.Moves.Count; index++)
            {
                var action = opening.Moves[index];
                if (action is < 0 or >= 81)
                {
                    throw new OpeningSuiteException(
                        $"Opening '{opening.Id}' action {index} must be between 0 and 80.");
                }

                var move = new Move(action / 9, action % 9);
                if (!UltimateTicTacToe.IsLegalMove(state, move, GameVariant.Standard))
                {
                    throw new OpeningSuiteException(
                        $"Opening '{opening.Id}' action {index} ({action}) is illegal.");
                }

                state = UltimateTicTacToe.ApplyMove(
                    state, move, GameVariant.Standard, seed: 0, useSpecial: false);
            }

            if (state.IsTerminal)
            {
                throw new OpeningSuiteException($"Opening '{opening.Id}' is already terminal.");
            }
        }

        if (!hasEmptyBoard)
        {
            throw new OpeningSuiteException("The opening suite must retain the empty-board opening.");
        }
    }
}
