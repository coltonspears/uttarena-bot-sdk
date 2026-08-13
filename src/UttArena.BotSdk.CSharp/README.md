# UttArena.BotSdk.CSharp

C# authoring host for UTT Arena bots. Implement `IBot` and hand it to
`BotConsoleHost.RunAsync`. The host reads newline-delimited protocol messages
from stdin, enforces the per-move deadline, validates your move against the
canonical legal-move list, and writes the response to stdout.

## Install

```bash
dotnet add package UttArena.BotSdk.CSharp --version 1.2.0
```

nuget.org is for **local authoring**. Arena submissions restore from an offline
feed with no network, so a submission zip must ship a `NuGet.config` that points
at the arena's package folder, not nuget.org. Copy the template's `NuGet.config`
when you package.

## Minimal bot

```csharp
using UttArena.BotSdk.CSharp;
using UttArena.Contracts;

internal sealed class MyBot : IBot
{
    public ValueTask<BotDecision> GetMoveAsync(
        MoveRequest request,
        CancellationToken cancellationToken)
        => ValueTask.FromResult<BotDecision>(request.LegalMoves[0]);
}

await BotConsoleHost.RunAsync(new MyBot());
```

`request.LegalMoves` is authoritative and is never empty on your turn. Returning
anything outside it forfeits the move.

- Banter: `new BotDecision(move, "Nice board.")`. Use
  `BanterSituations.Analyze(request)` for situation flags.
- Wildcard / Double Move: pick from `request.LegalSpecialMoves` and return
  `new BotDecision(move, UseSpecial: true)`. Ordinary bots can keep choosing
  from `LegalMoves`.
- Search: add `UttArena.GameEngine` and call `ProtocolBridge.ToGameState(request)`.
- Diagnostics: `BotConsoleLog.WriteAsync` / `InspectAsync` (stderr). Never write
  anything but protocol messages to stdout.

## Test locally

```bash
dotnet run -- --self-test   # if you copied the template
dotnet run --project tools/UttArena.BotHarness -- --conformance "dotnet run -c Release --project ."
```

## Submit

Zip the **contents** of the project folder (the `.csproj` at the archive root),
exclude `bin/` and `obj/`, and upload it on the arena **My bots** page. Then
promote a ready version and publish the bot.

Full workflow, limits, banned APIs, variants, and the protocol spec:

- Arena docs: `/docs/bots`
- Protocol: see the `UttArena.Contracts` package README and `PROTOCOL.md` in
  [uttarena-bot-sdk](https://github.com/coltonspears/uttarena-bot-sdk)
