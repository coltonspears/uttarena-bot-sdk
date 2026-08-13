using System.Text.Json;
using UttArena.Contracts;

namespace UttArena.BotSdk.CSharp;

/// <summary>
/// The complete bot authoring surface. The host supplies a validated request and cancellation token.
/// </summary>
public interface IBot
{
    ValueTask<BotDecision> GetMoveAsync(
        MoveRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// A legal move plus optional banter. Existing bots can return a bare
/// <see cref="BoardPosition"/> thanks to the implicit conversion.
/// </summary>
public readonly record struct BotDecision(
    BoardPosition Move,
    string? Banter = null,
    bool UseSpecial = false)
{
    public static implicit operator BotDecision(BoardPosition move) => new(move);
}

/// <summary>
/// Marks a method or type as banter/flavor so the weight-class classifier ignores it.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class BanterAttribute : Attribute;

public static class BotConsoleLog
{
    public static async ValueTask WriteAsync(
        ProtocolLogLevel level,
        string message,
        string? requestId = null,
        TextWriter? error = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        error ??= Console.Error;

        var log = new ProtocolLog(
            ProtocolConstants.Version,
            ProtocolConstants.LogType,
            DateTimeOffset.UtcNow,
            level,
            message,
            requestId);

        await error.WriteLineAsync(
            ProtocolJson.SerializeLine(log).AsMemory(),
            cancellationToken);
        await error.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Writes a structured debug dump to stderr. The arena surfaces these in the
    /// unlisted Debug tab; ranked matches ignore them for the feed.
    /// </summary>
    public static async ValueTask InspectAsync(
        string label,
        object? value,
        ProtocolLogLevel level = ProtocolLogLevel.Debug,
        string? requestId = null,
        TextWriter? error = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        error ??= Console.Error;

        JsonElement? data = null;
        if (value is not null)
        {
            data = JsonSerializer.SerializeToElement(value, ProtocolJson.Options);
        }

        var log = new ProtocolLog(
            ProtocolConstants.Version,
            ProtocolConstants.LogType,
            DateTimeOffset.UtcNow,
            level,
            label,
            requestId,
            Label: label,
            Data: data);

        await error.WriteLineAsync(
            ProtocolJson.SerializeLine(log).AsMemory(),
            cancellationToken);
        await error.FlushAsync(cancellationToken);
    }
}
