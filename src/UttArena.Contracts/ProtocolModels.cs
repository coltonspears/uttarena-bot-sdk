using System.Text.Json.Serialization;

namespace UttArena.Contracts;

/// <summary>
/// Variants whose bot-visible behavior is defined by protocol v1. Standard is
/// zero so requests written for ordinary games can omit the optional field.
/// </summary>
[JsonConverter(typeof(ProtocolGameVariantJsonConverter))]
public enum ProtocolGameVariant
{
    Standard = 0,
    Wildcard = 1,
    DoubleMove = 2,
    Chaos = 3,
    Anarchy = 4,
    SuddenDeath = 5,
    Territory = 6,
    Misere = 7,
    CenterControl = 8,
    LockedArena = 9,
}

public sealed class ProtocolGameVariantJsonConverter()
    : StableStringEnumJsonConverter<ProtocolGameVariant>(
        new Dictionary<ProtocolGameVariant, string>
        {
            [ProtocolGameVariant.Standard] = "standard",
            [ProtocolGameVariant.Wildcard] = "wildcard",
            [ProtocolGameVariant.DoubleMove] = "doubleMove",
            [ProtocolGameVariant.Chaos] = "chaos",
            [ProtocolGameVariant.Anarchy] = "anarchy",
            [ProtocolGameVariant.SuddenDeath] = "suddenDeath",
            [ProtocolGameVariant.Territory] = "territory",
            [ProtocolGameVariant.Misere] = "misere",
            [ProtocolGameVariant.CenterControl] = "centerControl",
            [ProtocolGameVariant.LockedArena] = "lockedArena",
        });

public sealed record SpecialAvailability(
    [property: JsonPropertyName("x")] bool X,
    [property: JsonPropertyName("o")] bool O)
{
    public bool IsAvailable(PlayerMark mark) => mark == PlayerMark.X ? X : O;
}

public sealed record BoardPosition(
    [property: JsonPropertyName("row")] int Row,
    [property: JsonPropertyName("column")] int Column);

public sealed record MoveRecord(
    [property: JsonPropertyName("ply")] int Ply,
    [property: JsonPropertyName("mark")] PlayerMark Mark,
    [property: JsonPropertyName("position")] BoardPosition Position);

public sealed record LocalBoardState(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("status")] LocalBoardStatus Status);

public sealed record BoardState(
    [property: JsonPropertyName("cells")] IReadOnlyList<CellState> Cells,
    [property: JsonPropertyName("localBoards")] IReadOnlyList<LocalBoardState> LocalBoards,
    [property: JsonPropertyName("activeLocalBoard")] int? ActiveLocalBoard,
    [property: JsonPropertyName("currentTurn")] PlayerMark CurrentTurn,
    [property: JsonPropertyName("moveNumber")] int MoveNumber);

public sealed record RulesetDescriptor(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("boardSize")] int BoardSize,
    [property: JsonPropertyName("localBoardSize")] int LocalBoardSize,
    [property: JsonPropertyName("sendMoveToLocalBoard")] bool SendMoveToLocalBoard,
    [property: JsonPropertyName("freeMoveWhenTargetBoardClosed")] bool FreeMoveWhenTargetBoardClosed)
{
    public static RulesetDescriptor Standard { get; } = new(
        ProtocolConstants.StandardRulesetId,
        ProtocolConstants.Version,
        ProtocolConstants.BoardSize,
        ProtocolConstants.LocalBoardSize,
        SendMoveToLocalBoard: true,
        FreeMoveWhenTargetBoardClosed: true);
}

public sealed record TimingControl(
    [property: JsonPropertyName("moveTimeoutMs")] int MoveTimeoutMs,
    [property: JsonPropertyName("initialBankMs")] long? InitialBankMs,
    [property: JsonPropertyName("incrementMs")] int IncrementMs);

public sealed record MoveTiming(
    [property: JsonPropertyName("moveTimeoutMs")] int MoveTimeoutMs,
    [property: JsonPropertyName("remainingBankMs")] long? RemainingBankMs,
    [property: JsonPropertyName("incrementMs")] int IncrementMs,
    [property: JsonPropertyName("deadlineUtc")] DateTimeOffset DeadlineUtc);

public sealed record OpponentMetadata(
    [property: JsonPropertyName("botId")] string BotId,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("version")] string? Version);
