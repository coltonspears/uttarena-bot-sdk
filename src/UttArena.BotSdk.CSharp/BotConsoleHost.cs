using System.Text.Json;
using UttArena.Contracts;

namespace UttArena.BotSdk.CSharp;

public static class BotConsoleHost
{
    public static async Task RunAsync(
        IBot bot,
        TextReader? input = null,
        TextWriter? output = null,
        TextWriter? error = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bot);
        input ??= Console.In;
        output ??= Console.Out;
        error ??= Console.Error;

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await input.ReadLineAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (line is null)
            {
                break;
            }

            await ProcessLineAsync(bot, line, output, error, cancellationToken);
        }
    }

    private static async Task ProcessLineAsync(
        IBot bot,
        string line,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        string? requestId = null;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("A protocol message must be a JSON object.");
            }

            requestId = ReadOptionalString(root, "requestId");
            var protocolVersion = ReadRequiredInt32(root, "protocolVersion");
            var type = ReadRequiredString(root, "type");

            if (protocolVersion != ProtocolConstants.Version)
            {
                await WriteErrorAsync(
                    output,
                    requestId,
                    ProtocolErrorCode.UnsupportedVersion,
                    $"Unsupported protocolVersion {protocolVersion}; expected {ProtocolConstants.Version}.",
                    fatal: false,
                    cancellationToken);
                return;
            }

            switch (type)
            {
                case ProtocolConstants.MatchStartType:
                    var matchStart = ProtocolJson.DeserializeLine<MatchStartMessage>(line);
                    ProtocolValidation.EnsureValid(matchStart);
                    await TryLogAsync(
                        error,
                        ProtocolLogLevel.Information,
                        $"Match '{matchStart.MatchId}' started.",
                        requestId: null,
                        cancellationToken);
                    break;

                case ProtocolConstants.MoveRequestType:
                    var request = ProtocolJson.DeserializeLine<MoveRequest>(line);
                    ProtocolValidation.EnsureValid(request);
                    await HandleMoveRequestAsync(bot, request, output, error, cancellationToken);
                    break;

                default:
                    await WriteErrorAsync(
                        output,
                        requestId,
                        ProtocolErrorCode.UnsupportedMessage,
                        $"Unsupported message type '{type}'.",
                        fatal: false,
                        cancellationToken);
                    break;
            }
        }
        catch (JsonException exception)
        {
            await TryLogAsync(
                error,
                ProtocolLogLevel.Warning,
                $"Rejected malformed protocol input: {exception.Message}",
                requestId,
                cancellationToken);
            await WriteErrorAsync(
                output,
                requestId,
                ProtocolErrorCode.MalformedRequest,
                "The input line is not a well-formed protocol message.",
                fatal: false,
                cancellationToken);
        }
        catch (ProtocolValidationException exception)
        {
            await TryLogAsync(
                error,
                ProtocolLogLevel.Warning,
                $"Rejected invalid protocol input: {exception.Message}",
                requestId,
                cancellationToken);
            await WriteErrorAsync(
                output,
                requestId,
                ProtocolErrorCode.ValidationFailed,
                exception.Message,
                fatal: false,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        catch (Exception exception)
        {
            await TryLogAsync(
                error,
                ProtocolLogLevel.Error,
                $"Unexpected host failure: {exception}",
                requestId,
                cancellationToken);
            await WriteErrorAsync(
                output,
                requestId,
                ProtocolErrorCode.InternalError,
                "The bot host could not process the request.",
                fatal: false,
                cancellationToken);
        }
    }

    private static async Task HandleMoveRequestAsync(
        IBot bot,
        MoveRequest request,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var timeUntilDeadline = request.Timing.DeadlineUtc - DateTimeOffset.UtcNow;
        var moveTimeout = TimeSpan.FromMilliseconds(request.Timing.MoveTimeoutMs);
        var effectiveTimeout = timeUntilDeadline < moveTimeout ? timeUntilDeadline : moveTimeout;

        if (effectiveTimeout <= TimeSpan.Zero)
        {
            await WriteErrorAsync(
                output,
                request.RequestId,
                ProtocolErrorCode.MoveTimedOut,
                "The move deadline has already passed.",
                fatal: false,
                cancellationToken);
            return;
        }

        using var moveCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        moveCancellation.CancelAfter(effectiveTimeout);

        BotDecision decision;
        try
        {
            decision = await bot
                .GetMoveAsync(request, moveCancellation.Token)
                .AsTask()
                .WaitAsync(moveCancellation.Token);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested &&
            moveCancellation.IsCancellationRequested)
        {
            await TryLogAsync(
                error,
                ProtocolLogLevel.Warning,
                "Bot exceeded its move deadline.",
                request.RequestId,
                cancellationToken);
            await WriteErrorAsync(
                output,
                request.RequestId,
                ProtocolErrorCode.MoveTimedOut,
                "The bot did not return a move before the deadline.",
                fatal: false,
                cancellationToken);
            return;
        }
        catch (Exception exception)
        {
            await TryLogAsync(
                error,
                ProtocolLogLevel.Error,
                $"Bot failed while selecting a move: {exception}",
                request.RequestId,
                cancellationToken);
            await WriteErrorAsync(
                output,
                request.RequestId,
                ProtocolErrorCode.BotFailure,
                "The bot failed while selecting a move.",
                fatal: false,
                cancellationToken);
            return;
        }

        var moveValidation = ProtocolValidation.ValidateMove(
            request, decision.Move, decision.UseSpecial);
        if (!moveValidation.IsValid)
        {
            var message = string.Join("; ", moveValidation.Errors);
            await TryLogAsync(
                error,
                ProtocolLogLevel.Warning,
                $"Bot returned an illegal move: {message}",
                request.RequestId,
                cancellationToken);
            await WriteErrorAsync(
                output,
                request.RequestId,
                ProtocolErrorCode.IllegalMove,
                message,
                fatal: false,
                cancellationToken);
            return;
        }

        var response = new MoveResponse(
            ProtocolConstants.Version,
            ProtocolConstants.MoveResponseType,
            request.RequestId,
            decision.Move,
            ProtocolValidation.NormalizeBanter(decision.Banter),
            decision.UseSpecial);

        ProtocolValidation.EnsureValid(response);
        await WriteLineAsync(output, response, cancellationToken);
    }

    private static async Task WriteErrorAsync(
        TextWriter output,
        string? requestId,
        ProtocolErrorCode code,
        string message,
        bool fatal,
        CancellationToken cancellationToken)
    {
        var protocolError = new ProtocolError(
            ProtocolConstants.Version,
            ProtocolConstants.ErrorType,
            requestId,
            code,
            message,
            fatal);

        await WriteLineAsync(output, protocolError, cancellationToken);
    }

    private static async Task WriteLineAsync<T>(
        TextWriter output,
        T message,
        CancellationToken cancellationToken)
    {
        await output.WriteLineAsync(
            ProtocolJson.SerializeLine(message).AsMemory(),
            cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private static async Task TryLogAsync(
        TextWriter error,
        ProtocolLogLevel level,
        string message,
        string? requestId,
        CancellationToken cancellationToken)
    {
        try
        {
            await BotConsoleLog.WriteAsync(
                level,
                message,
                requestId,
                error,
                cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A broken stderr stream must never corrupt or block the stdout protocol.
        }
    }

    private static string ReadRequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"'{propertyName}' must be a string.");
        }

        return property.GetString()
            ?? throw new JsonException($"'{propertyName}' cannot be null.");
    }

    private static string? ReadOptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"'{propertyName}' must be a string when present.");
        }

        return property.GetString();
    }

    private static int ReadRequiredInt32(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt32(out var value))
        {
            throw new JsonException($"'{propertyName}' must be a 32-bit integer.");
        }

        return value;
    }
}
