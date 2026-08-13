using System.Text.Json;
using System.Text.Json.Serialization;

namespace UttArena.Contracts;

public static class ProtocolConstants
{
    public const int Version = 1;
    public const string MatchStartType = "match_start";
    public const string MoveRequestType = "move_request";
    public const string MoveResponseType = "move_response";
    public const string ErrorType = "error";
    public const string LogType = "log";
    public const string StandardRulesetId = "ultimate-tic-tac-toe";
    public const int BoardSize = 9;
    public const int LocalBoardSize = 3;
    public const int CellCount = BoardSize * BoardSize;
    public const int LocalBoardCount = LocalBoardSize * LocalBoardSize;
}

/// <summary>
/// SDK 1.2 understands extra variant strings and optional <c>randomSeed</c>.
/// The arena only emits those to bots whose PackageReference is 1.2 or later,
/// because 1.1 hosts reject unknown enum values and unknown properties.
/// </summary>
public static class ProtocolCompatibility
{
    public static readonly Version ExtendedVariantSdk = new(1, 2, 0);

    public static bool SupportsExtendedVariants(string? sdkVersion) =>
        Version.TryParse(sdkVersion, out var parsed) && parsed >= ExtendedVariantSdk;
}

[JsonConverter(typeof(PlayerMarkJsonConverter))]
public enum PlayerMark
{
    X = 1,
    O = 2,
}

[JsonConverter(typeof(CellStateJsonConverter))]
public enum CellState
{
    Empty = 0,
    X = 1,
    O = 2,
}

[JsonConverter(typeof(LocalBoardStatusJsonConverter))]
public enum LocalBoardStatus
{
    Open = 0,
    WonByX = 1,
    WonByO = 2,
    Draw = 3,
}

[JsonConverter(typeof(ProtocolErrorCodeJsonConverter))]
public enum ProtocolErrorCode
{
    MalformedRequest = 1,
    UnsupportedVersion = 2,
    UnsupportedMessage = 3,
    ValidationFailed = 4,
    MoveTimedOut = 5,
    BotFailure = 6,
    IllegalMove = 7,
    InternalError = 8,
}

[JsonConverter(typeof(ProtocolLogLevelJsonConverter))]
public enum ProtocolLogLevel
{
    Trace = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
}

public sealed class PlayerMarkJsonConverter()
    : StableStringEnumJsonConverter<PlayerMark>(
        new Dictionary<PlayerMark, string>
        {
            [PlayerMark.X] = "x",
            [PlayerMark.O] = "o",
        })
{
}

public sealed class CellStateJsonConverter()
    : StableStringEnumJsonConverter<CellState>(
        new Dictionary<CellState, string>
        {
            [CellState.Empty] = "empty",
            [CellState.X] = "x",
            [CellState.O] = "o",
        })
{
}

public sealed class LocalBoardStatusJsonConverter()
    : StableStringEnumJsonConverter<LocalBoardStatus>(
        new Dictionary<LocalBoardStatus, string>
        {
            [LocalBoardStatus.Open] = "open",
            [LocalBoardStatus.WonByX] = "wonByX",
            [LocalBoardStatus.WonByO] = "wonByO",
            [LocalBoardStatus.Draw] = "draw",
        })
{
}

public sealed class ProtocolErrorCodeJsonConverter()
    : StableStringEnumJsonConverter<ProtocolErrorCode>(
        new Dictionary<ProtocolErrorCode, string>
        {
            [ProtocolErrorCode.MalformedRequest] = "malformedRequest",
            [ProtocolErrorCode.UnsupportedVersion] = "unsupportedVersion",
            [ProtocolErrorCode.UnsupportedMessage] = "unsupportedMessage",
            [ProtocolErrorCode.ValidationFailed] = "validationFailed",
            [ProtocolErrorCode.MoveTimedOut] = "moveTimedOut",
            [ProtocolErrorCode.BotFailure] = "botFailure",
            [ProtocolErrorCode.IllegalMove] = "illegalMove",
            [ProtocolErrorCode.InternalError] = "internalError",
        })
{
}

public sealed class ProtocolLogLevelJsonConverter()
    : StableStringEnumJsonConverter<ProtocolLogLevel>(
        new Dictionary<ProtocolLogLevel, string>
        {
            [ProtocolLogLevel.Trace] = "trace",
            [ProtocolLogLevel.Debug] = "debug",
            [ProtocolLogLevel.Information] = "information",
            [ProtocolLogLevel.Warning] = "warning",
            [ProtocolLogLevel.Error] = "error",
        })
{
}

public abstract class StableStringEnumJsonConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private readonly IReadOnlyDictionary<TEnum, string> _toWire;
    private readonly IReadOnlyDictionary<string, TEnum> _fromWire;

    protected StableStringEnumJsonConverter(IReadOnlyDictionary<TEnum, string> values)
    {
        _toWire = values;
        _fromWire = values.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);
    }

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"{typeof(TEnum).Name} must be a string.");
        }

        var value = reader.GetString();
        if (value is null || !_fromWire.TryGetValue(value, out var result))
        {
            throw new JsonException($"Unknown {typeof(TEnum).Name} value '{value}'.");
        }

        return result;
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        if (!_toWire.TryGetValue(value, out var wireValue))
        {
            throw new JsonException($"Unknown {typeof(TEnum).Name} value '{value}'.");
        }

        writer.WriteStringValue(wireValue);
    }
}
