using UttArena.BotSdk.CSharp;
using UttArena.Contracts;

// Tier 1 reference bot: uniformly random legal move.
//
// This is the rating floor. Anything you write should beat it comfortably; if it
// does not, the bug is in your bot rather than in the arena.

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

try
{
    await BotConsoleHost.RunAsync(new RandomLegalMoveBot(), cancellationToken: shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    // Ctrl+C is a normal shutdown path.
}

internal sealed class RandomLegalMoveBot : IBot
{
    public ValueTask<BotDecision> GetMoveAsync(
        MoveRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<BotDecision>(
            request.LegalMoves[Random.Shared.Next(request.LegalMoves.Count)]);
    }
}
