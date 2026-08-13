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

```bash
./scripts/pack-bot-sdk.sh
dotnet run --project tools/UttArena.BotHarness -- \
  --conformance "dotnet run -c Release --project samples/bot-template-csharp"
```

Arena submissions restore from an **offline** feed (no network). Keep the
template `NuGet.config` in any zip you upload.

## Docs

- [Writing a bot](docs/BOT_AUTHORING.md)
- [Protocol](PROTOCOL.md)
- [Game modes](docs/GAME_MODES.md)
- In-app guide on the arena site: `/docs/bots`

## Publishing packages

Package publishing to nuget.org is still driven from the private arena
repository (`sdk-v*` tags and NuGet Trusted Publishing). After this public repo
exists, a second Trusted Publishing policy can be added for it.
