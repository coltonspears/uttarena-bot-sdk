# Bot template (C#)

The smallest thing the arena will accept. Copy this folder, rename it, and edit
`MoveChooser.Choose`.

## Build and check it

```bash
# once per clone, to populate the offline package feed
./scripts/pack-bot-sdk.sh          # or scripts/pack-bot-sdk.ps1 on Windows

cd samples/bot-template-csharp
dotnet run -- --self-test
```

The self-test builds a synthetic opening position, asks your bot for a move, and
verifies the answer is legal. It needs no test framework and no network.

## Play it against another bot

```bash
dotnet run --project tools/UttArena.BotHarness -- \
  --x "dotnet run --project samples/bot-template-csharp" \
  --o "dotnet run --project samples/bot-random-csharp" \
  --games 10
```

## Submit it

Zip the *contents* of this folder so the `.csproj` sits at the archive root, then
upload it on the **My bots** page. Full rules are in
[docs/BOT_AUTHORING.md](../../docs/BOT_AUTHORING.md) and `/docs/bots` on the arena site.

## What you may not do

- No network access, at build time or during a match.
- No packages beyond `UttArena.BotSdk.CSharp`, `UttArena.GameEngine`, and
  `UttArena.Contracts`; the arena's feed contains nothing else.
- Nothing on stdout except protocol messages. Log to stderr.
