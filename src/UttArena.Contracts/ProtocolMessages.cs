using System.Text.Json;
using System.Text.Json.Serialization;

namespace UttArena.Contracts;

public sealed record ProtocolEnvelope(
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("requestId")] string? RequestId);

public sealed record MatchStartMessage(
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("matchId")] string MatchId,
    [property: JsonPropertyName("botMark")] PlayerMark BotMark,
    [property: JsonPropertyName("opponent")] OpponentMetadata Opponent,
    [property: JsonPropertyName("ruleset")] RulesetDescriptor Ruleset,
    [property: JsonPropertyName("timing")] TimingControl Timing);

public sealed record MoveRequest(
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("matchId")] string MatchId,
    [property: JsonPropertyName("botMark")] PlayerMark BotMark,
    [property: JsonPropertyName("board")] BoardState Board,
    [property: JsonPropertyName("history")] IReadOnlyList<MoveRecord> History,
    [property: JsonPropertyName("legalMoves")] IReadOnlyList<BoardPosition> LegalMoves,
    [property: JsonPropertyName("opponent")] OpponentMetadata Opponent,
    [property: JsonPropertyName("ruleset")] RulesetDescriptor Ruleset,
    [property: JsonPropertyName("timing")] MoveTiming Timing,
    [property: JsonPropertyName("variant")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    ProtocolGameVariant Variant = ProtocolGameVariant.Standard,
    [property: JsonPropertyName("specialAvailability")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SpecialAvailability? SpecialAvailability = null,
    [property: JsonPropertyName("doubleMovePending")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool DoubleMovePending = false,
    [property: JsonPropertyName("legalSpecialMoves")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<BoardPosition>? LegalSpecialMoves = null,
    [property: JsonPropertyName("randomSeed")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ulong? RandomSeed = null);

public sealed record MoveResponse(
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("move")] BoardPosition Move,
    [property: JsonPropertyName("banter")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Banter = null,
    [property: JsonPropertyName("useSpecial")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool UseSpecial = false);

public sealed record ProtocolError(
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("requestId")] string? RequestId,
    [property: JsonPropertyName("code")] ProtocolErrorCode Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("fatal")] bool Fatal);

public sealed record ProtocolLog(
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("timestampUtc")] DateTimeOffset TimestampUtc,
    [property: JsonPropertyName("level")] ProtocolLogLevel Level,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("requestId")] string? RequestId,
    [property: JsonPropertyName("label")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Label = null,
    [property: JsonPropertyName("data")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    JsonElement? Data = null);

public static class ProtocolLimits
{
    public const int MaxBanterLength = 120;
    public const int MaxBantersPerMatch = 5;
    public const int MinPliesBetweenBanters = 3;
    public const int MaxDebugDataBytes = 32 * 1024;
    public const int MaxBotDebugObjectsPerMatch = 40;
}

public static class ProtocolJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string SerializeLine<T>(T message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return JsonSerializer.Serialize(message, Options);
    }

    public static T DeserializeLine<T>(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            throw new JsonException("A protocol line cannot be empty.");
        }

        return JsonSerializer.Deserialize<T>(line, Options)
            ?? throw new JsonException($"The protocol line did not contain a {typeof(T).Name}.");
    }

    private static JsonSerializerOptions CreateOptions() => new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
