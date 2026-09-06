# UTT Arena Bot SDK

Public C# packages, protocol, samples, and local harness for writing bots that
play Ultimate Tic-Tac-Toe on UTT Arena.

The arena website, match runner, and account system are separate. This
repository is only what bot authors need.

## Packages (nuget.org)

| Package | Role |
| --- | --- |
| `UttArena.BotSdk.CSharp` | Implement `IBot`, run `BotConsoleHost` |
| `UttArena.Contracts` | Protocol messages and validation |
| `UttArena.GameEngine` | Canonical rules for search bots |

Current version is in `Directory.Build.props` (`UttArenaSdkVersion`).

```bash
dotnet new console -n MyBot -f net8.0
cd MyBot
dotnet add package UttArena.BotSdk.CSharp
```

Copy `samples/bot-template-csharp` if you want a ready `NuGet.config`, self-test,
and submission layout.

## Local testing

From a fresh clone, pack the local dependencies and build the sample before
starting the protocol harness. Running the built DLL keeps build output out of
the bot's JSON protocol stream.

**PowerShell:**

```powershell
.\scripts\pack-bot-sdk.ps1
dotnet build samples/bot-template-csharp -c Release
dotnet samples/bot-template-csharp/bin/Release/net8.0/bot-template-csharp.dll --self-test
dotnet run --project tools/UttArena.BotHarness -- --conformance "dotnet samples/bot-template-csharp/bin/Release/net8.0/bot-template-csharp.dll"
```

**Bash:**

```bash
./scripts/pack-bot-sdk.sh
dotnet build samples/bot-template-csharp -c Release
dotnet samples/bot-template-csharp/bin/Release/net8.0/bot-template-csharp.dll --self-test
dotnet run --project tools/UttArena.BotHarness -- \
  --conformance "dotnet samples/bot-template-csharp/bin/Release/net8.0/bot-template-csharp.dll"
```

The conformance run exercises seven fixtures, including forced-board moves,
free choice after a closed board, and game-mode special actions. A successful
run ends with `7/7 fixtures passed.` The template chooses a local center when
available and otherwise the first legal move; it is a starting point for your
own strategy.

Arena submissions restore from an **offline** feed (no network). Keep the
template `NuGet.config` in any zip you upload.

## Docs

- [Writing a bot](docs/BOT_AUTHORING.md)
- [Protocol](PROTOCOL.md)
- [Game modes](docs/GAME_MODES.md)
- In-app guide on the arena site: `/docs/bots`

## Publishing packages

Package publishing to nuget.org is driven from the private arena repository
(`sdk-v*` tags and NuGet Trusted Publishing). A second nuget.org Trusted
Publishing policy can be added for this public repository.
