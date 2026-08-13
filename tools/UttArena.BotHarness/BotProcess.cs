using System.Diagnostics;
using System.Text;
using System.Text.Json;
using UttArena.Contracts;

namespace UttArena.BotHarness;

public sealed class BotTimeoutException(string message) : Exception(message);

public sealed class BotProtocolException(string message) : Exception(message);

public sealed class BotCrashException(string message) : Exception(message);

/// <summary>
/// Runs a bot as a child process and talks to it over the real newline-delimited
/// JSON protocol, exactly as the arena's match runner does. Nothing here is
/// simulated, so a bot that passes locally behaves the same way in a real match.
/// </summary>
public sealed class BotProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _diagnostics = new();
    private readonly Task _diagnosticsPump;

    private BotProcess(string name, Process process)
    {
        Name = name;
        _process = process;
        _diagnosticsPump = PumpDiagnosticsAsync();
    }

    public string Name { get; }

    public string Diagnostics => _diagnostics.ToString();

    public static BotProcess Start(string name, string commandLine, string? workingDirectory = null)
    {
        var (executable, arguments) = SplitCommand(commandLine);
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory()
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{commandLine}'.");
        return new BotProcess(name, process);
    }

    public async Task SendAsync<T>(T message, CancellationToken cancellationToken)
    {
        try
        {
            await _process.StandardInput.WriteLineAsync(
                ProtocolJson.SerializeLine(message).AsMemory(),
                cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            throw new BotCrashException($"{Name} stopped accepting protocol input: {exception.Message}");
        }
    }

    public async Task<MoveResponse> RequestMoveAsync(
        MoveRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await SendAsync(request, cancellationToken);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        string? line;
        try
        {
            line = await _process.StandardOutput.ReadLineAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BotTimeoutException($"{Name} did not answer within {timeout.TotalMilliseconds:F0} ms.");
        }

        if (line is null)
        {
            if (_process.HasExited)
            {
                throw new BotCrashException(
                    $"{Name} exited with code {_process.ExitCode} without answering.");
            }
            throw new BotProtocolException($"{Name} closed stdout without answering.");
        }

        try
        {
            // A bot may legitimately report a protocol error instead of a move; the
            // arena treats that as a failed turn, and so does the harness.
            if (line.Contains("\"type\":\"error\"", StringComparison.Ordinal))
            {
                var error = ProtocolJson.DeserializeLine<ProtocolError>(line);
                throw new BotProtocolException($"{Name} returned {error.Code}: {error.Message}");
            }

            var response = ProtocolJson.DeserializeLine<MoveResponse>(line);
            ProtocolValidation.EnsureValid(response);
            if (!string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal))
            {
                throw new BotProtocolException(
                    $"{Name} answered requestId '{response.RequestId}' but was asked '{request.RequestId}'.");
            }

            return response;
        }
        catch (JsonException exception)
        {
            throw new BotProtocolException(
                $"{Name} returned malformed protocol JSON: {exception.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit(1_000))
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (InvalidOperationException)
        {
            // The process already went away.
        }

        await _diagnosticsPump.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        _process.Dispose();
    }

    private async Task PumpDiagnosticsAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                _diagnostics.AppendLine(line);
            }
        }
        catch (Exception)
        {
            // Diagnostics are best effort and must never fail a game.
        }
    }

    private static (string Executable, IReadOnlyList<string> Arguments) SplitCommand(string commandLine)
    {
        var parts = Tokenize(commandLine);
        if (parts.Count == 0)
        {
            throw new ArgumentException("A bot command cannot be empty.", nameof(commandLine));
        }

        return (parts[0], parts.Skip(1).ToArray());
    }

    private static List<string> Tokenize(string value)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        foreach (var character in value)
        {
            if (character == '"')
            {
                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(character);
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
