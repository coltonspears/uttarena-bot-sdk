# Writing a bot for UTT Arena

This is the whole path from an empty folder to a promoted bot that plays ranked
matches. It takes about ten minutes the first time.

A bot is a console program. The arena sends it one JSON object per line on stdin
and expects one JSON object per line back on stdout. That is the entire contract,
so any language can play, but C# authors get an SDK that hides the loop.

- [1. Scaffold](#1-scaffold)
- [2. Write the move](#2-write-the-move)
- [3. Test locally](#3-test-locally)
- [4. Package](#4-package)
- [5. Upload and submit](#5-upload-and-submit)
- [6. Watch the build](#6-watch-the-build)
- [7. Promote](#7-promote)
- [8. Publish](#8-publish)
- [Time controls](#time-controls)
- [Limits and rules](#limits-and-rules)
- [Troubleshooting](#troubleshooting)

## 1. Scaffold

The fastest path on a laptop is nuget.org:

```bash
dotnet new console -n MyBot -f net8.0
cd MyBot
dotnet add package UttArena.BotSdk.CSharp --version 1.2.0
```

Copy [samples/bot-template-csharp](https://github.com/coltonspears/uttarena-bot-sdk/tree/main/samples/bot-template-csharp)
if you want a ready `NuGet.config`, `--self-test`, and the zip layout the arena
expects. The public SDK repo is
[uttarena-bot-sdk](https://github.com/coltonspears/uttarena-bot-sdk); in-app docs
are at `/docs/bots`.

If you are working in the arena monorepo, populate the local package feed once
per clone, then copy the template:

```bash
./scripts/pack-bot-sdk.sh            # scripts/pack-bot-sdk.ps1 on Windows
cp -r samples/bot-template-csharp ../my-bot
```

The feed at `./.packages` holds the same three packages the arena's sandboxed
builder produces: `UttArena.BotSdk.CSharp`, `UttArena.GameEngine`, and
`UttArena.Contracts`. Your project restores from that feed and nothing else, so a
package that resolves for you also resolves in the arena. Re-run the script
whenever you pull a change to the SDK.

For local authoring only, the same packages are also published on
[nuget.org](https://www.nuget.org/packages/UttArena.BotSdk.CSharp). You can
`dotnet add package UttArena.BotSdk.CSharp --version 1.2.0` from a normal
NuGet.config. **Do not** point a submission zip at nuget.org — the arena build
has no network and restores only from its offline feed. Keep the template
`NuGet.config` when packaging for upload.

The template's `NuGet.config` is what makes this work, and it must travel with
your project:

```xml
<packageSources>
  <clear />
  <add key="uttarena-offline" value="../../.packages" />
</packageSources>
<packageSourceMapping>
  <clear />
</packageSourceMapping>
```

`<clear />` in both sections matters. The first removes nuget.org, which the
arena cannot reach. The second removes any machine-wide source mapping that would
otherwise route every package away from the offline feed.

## 2. Write the move

Implement `IBot`. `BotConsoleHost` reads stdin, enforces your deadline, validates
your answer, and writes the response:

```csharp
internal sealed class MyBot : IBot
{
    public ValueTask<BotDecision> GetMoveAsync(
        MoveRequest request,
        CancellationToken cancellationToken)
    {
        return ValueTask.FromResult<BotDecision>(request.LegalMoves[0]);
    }
}
```

Three things about `MoveRequest` are worth internalising:

- `LegalMoves` is authoritative and is never empty on your turn. Returning
  anything outside it forfeits the move, and the arena scores a forfeit as a loss.
- Positions are `row` and `column` in 0..8 across the full 9x9 grid, not
  board-and-cell indices. `ProtocolBridge` in `UttArena.GameEngine` converts
  between the two if you want to run a search.
- `Timing.DeadlineUtc` is the hard wall. `Timing.MoveTimeoutMs` is the budget for
  this move. Spend less than the budget: serialization, process scheduling, and
  the container's CPU share all come out of the same clock. The minimax and MCTS
  samples reserve 40% as headroom.

Wildcard and DoubleMove requests keep `LegalMoves` unchanged, so an existing bot
can ignore specials and continue playing ordinary legal moves. A special-aware
bot can inspect `request.Variant`, `request.SpecialAvailability`,
`request.DoubleMovePending`, and `request.LegalSpecialMoves`. To activate:

```csharp
if (request.LegalSpecialMoves is { Count: > 0 } specialMoves)
{
    return ValueTask.FromResult(
        new BotDecision(specialMoves[0], UseSpecial: true));
}
```

Wildcard special moves bypass the forced board once. DoubleMove special moves
obey ordinary routing and start a two-move turn; the next request has
`DoubleMovePending == true`, the same `BotMark`, ordinary `LegalMoves`, and no
`LegalSpecialMoves`. The game engine remains the legality source of truth. A
search bot can use `ProtocolBridge.ToGameState(request)` to preserve the variant,
both players' availability, and pending state.

Optional banter: return `new BotDecision(move, "Nice board.")`. Use
`BanterSituations.Analyze(request)` for situation flags (local threats, free
moves, opponent just took a board, …) and write your own lines. The arena accepts
at most five banters per match, at least three plies apart, ≤120 characters.

Weight class is scored at build time from move-logic structure (C# via Roslyn).
Identifier names do not matter. Comments, string literal contents, files under
`Banter/`, `*Banter*.cs`, and members marked `[Banter]` are excluded so flavour
text cannot push a bot into a heavier class.

Anything you print to stdout that is not a protocol message corrupts the stream
and fails the match. Use `BotConsoleLog` (stderr) for diagnostics, or
`BotConsoleLog.InspectAsync(label, value)` to dump objects into the unlisted
Debug console.

For search bots, `ProtocolBridge.ToGameState(request)` hands you the position as
a `GameState` you can feed to `UltimateTicTacToe.GetLegalMoves` and
`ApplyMove`, which are the same rules the arena adjudicates with.

## 3. Test locally

Fastest check, no framework required:

```bash
cd ../my-bot
dotnet run -- --self-test
```

Then use the harness. It runs two bots as real child processes over the real
protocol against the real engine, with no Docker and no API:

```bash
# Does my bot answer every position legally and inside its budget?
dotnet run --project tools/UttArena.BotHarness -- \
  --conformance "dotnet run -c Release --project ../my-bot"

# How does it score against a reference bot?
dotnet run --project tools/UttArena.BotHarness -- \
  --x "dotnet run -c Release --project ../my-bot" \
  --o "dotnet run -c Release --project samples/bot-minimax-csharp" \
  --games 10 --move-timeout-ms 1000

# Produce a reproducible, machine-readable paired-opening benchmark.
dotnet run --project tools/UttArena.BotHarness -- \
  --x "dotnet run -c Release --project ../my-bot" \
  --o "dotnet run -c Release --project samples/bot-minimax-csharp" \
  --openings training/uttarena-ml/config/openings-standard-v1.json \
  --seed 20260810 --json-out artifacts/my-bot-vs-minimax.json
```

Sides swap every game, so a series result is not distorted by the first-player
advantage. A bot that overruns its budget, crashes, or answers illegally forfeits
that game and the harness exits non-zero, which makes it usable in CI.

Useful flags: `--lenient-timeouts` reports slow moves instead of forfeiting them
while you profile, `--verbose` echoes each bot's stderr, and `--variant wildcard`
or `--variant doubleMove` runs a special-variant series. `--json-out` atomically
writes a versioned report with per-game assignments, seeds, outcomes, failures,
and timing statistics. `--openings` accepts the versioned Standard action-prefix
schema and plays every opening exactly twice with colors swapped; it is
deliberately incompatible with `--games` so the game count is unambiguous.

Four reference bots ship in `samples/`, in increasing strength: `bot-random`,
`bot-heuristic`, `bot-minimax`, `bot-mcts`, then `bot-challenge` (iterative-deepening
alpha-beta with meta-aware eval — the hardest non-ML practice opponent). Beating
`bot-minimax` in a 10-game series is a reasonable bar before submitting; use
`bot-challenge` when you want a tougher local sparring partner.

## 4. Package

Zip the *contents* of your project folder, not the folder itself:

```bash
cd ../my-bot
zip -r ../my-bot.zip . -x '*/bin/*' '*/obj/*' 'bin/*' 'obj/*'
```

The builder finds your project with `find . -maxdepth 3 -name '*.csproj' | sort |
head -n 1`. Two consequences:

- The `.csproj` must be at most three directory levels deep in the archive.
- If the archive contains more than one `.csproj`, the first one alphabetically
  wins. Ship exactly one project.

Excluding `bin/` and `obj/` is not just tidiness: they count against the archive
size and file-count limits.

## 5. Upload and submit

On the **My bots** page: name your bot, pick C#, drop the zip, and submit. The
page chains the two calls for you. Directly against the API it is:

```bash
# 1. Store the archive. Returns sourceBlobPath and sourceSha256.
curl -X POST http://localhost:5258/api/bots/artifacts \
  -H "Authorization: Bearer $TOKEN" -F file=@../my-bot.zip

# 2. Create the bot once.
curl -X POST http://localhost:5258/api/bots \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"name":"My Bot","language":"csharp"}'

# 3. Submit a version, which queues the build.
curl -X POST http://localhost:5258/api/bots/$BOT_ID/versions \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"sourceBlobPath":"...","sourceSha256":"..."}'
```

`sourceSha256` is optional. If you send it, the server re-hashes the stored
archive and rejects the submission on a mismatch, which catches a truncated
upload before a build burns a queue slot. Sizes are always measured server-side;
you cannot declare them.

Create the bot once and submit a new version for every iteration. Versions are
numbered automatically and keep their own rating and games-played counts.

## 6. Watch the build

A version moves through `Pending` (queued), `Building` (compiling in the
sandbox), and then either `Ready` or `BuildFailed`. The bots page polls this and
shows a build log for failures; owners can also read it directly:

```
GET /api/bots/{botId}/versions/{versionId}/build-log
```

The log is owner-only and capped at 64 KiB.

The build runs `docker build --network=none` against a prepared builder image.
There is no network and no nuget.org, only `/uttarena/packages`. The build has
120 seconds. Almost every first-time failure is a package that exists on the
author's machine but not in the offline feed, which is exactly what step 1's feed
is designed to catch before you upload.

## 7. Promote

Promotion is what makes a version the bot's active one and marks the bot ranked
eligible. Only a `Ready` version can be promoted; the button is disabled
otherwise. Use **Challenge** on the bots page to play an exhibition game against
a version first, which is the quickest way to sanity-check a build in the arena's
own sandbox rather than on your machine.

## 8. Publish

A bot is **private** when created: only you can see it and only you can play it.
Making it **public** puts it in the bot arena, where anyone can challenge it at
any time control. Use the visibility toggle on the bots page, or:

```bash
curl -X POST http://localhost:5258/api/bots/$BOT_ID/visibility \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"visibility":"public","description":"Minimax with a centre bias."}'
```

The optional summary (160 characters) is shown on arena cards. The description
(2000 characters) is shown on the bot profile. Both can be edited on My bots
without changing visibility. You can also restrict which modes the bot accepts
challenges in.

Publishing requires a promoted active version, and challengers may only play that
version — nobody can run a build you are still working on. Iterating stays safe:
submit a new version, watch it build, and promote it when you are happy; until
then the arena keeps serving the promoted one. Switching back to `private`
withdraws the bot from the arena and blocks new challenges.

Public bots keep a rating per time class, and the arena derives a difficulty band
from the rating in the requested class (`beginner` under 1050, `intermediate`
under 1250, `advanced` under 1450, `expert` above) so players can pick an opponent
near their level.

## Time controls

Bots play every standard time control: Bullet 1+0, Blitz 3+2, Rapid 10+5,
Classical 30+0, and Unlimited. Chaos, Anarchy, Wildcard, Sudden Death, Territory,
Misère, Center Control, Locked Arena, and Double Move are also bot-eligible.
Draft Opening, Relay, and Last Stand stay human-only. SDK 1.1 bots receive
`variant: standard` (except Wildcard / Double Move) so they keep parsing; SDK 1.2
bots receive the real variant name and Chaos `randomSeed`. Other variants are
rejected for ordinary challenges, although protocol-supported variants may be
available to a bot owner in an **unlisted debug** match. Unlisted games stay off
your profile and open a Debug console with the exact `move_request` board the bot
received.

This matters for how you spend time. In a timed mode the per-move budget is the
seat's whole remaining bank, not a fixed slice of it: `Timing.MoveTimeoutMs` is
what is left on your clock, and `Timing.DeadlineUtc` is the hard wall. Spending it
all on move one leaves nothing for move two, and running out is a loss like any
other. Budget against the bank rather than the deadline — a fraction of the
remaining time per move is the usual approach — and keep the samples' 40% headroom
for serialization and the container's CPU share.

Unlimited keeps the flat five-second per-move budget the canonical ruleset has
always used, which is what the harness defaults to.

## Limits and rules

| Limit | Value |
| --- | --- |
| Archive size (compressed) | 2 MiB |
| Archive size (expanded) | 16 MiB |
| Files in archive | 512 |
| Compiled output | 32 MiB |
| Build time | 120 seconds |
| Build network | none |
| Package sources | the arena's offline feed only |
| Stored build log | 64 KiB |

Archives are also rejected for containing symlinks or paths that escape the
extraction root.

At match time your bot runs as an unprivileged user in a container with no
network, so it cannot reach the filesystem outside `/bot`, open sockets, or see
other bots.

The Open-class build **rejects** source that uses network APIs, reflection or
dynamic loading, encryption (AES, RSA, and similar), or compression streams.
SHA-256 / SHA-384 / SHA-512 hashing is allowed so a policy/value bot can checksum
weights. Those bans apply in every weight class; they are not a way to jump a
class.

## Training a policy/value bot

To train a hierarchical policy/value network with self-play, track generations
in MLflow, and package a checkpoint for this upload flow, see
[`ML_TRAINING.md`](ML_TRAINING.md). The C# sample that loads exported weights is
`samples/bot-policy-value-csharp`; `training/scripts/deploy-policy-value-bot.ps1`
builds a submission zip from a `generation-NNNNNN.pt` checkpoint.

## Publishing the Bot SDK (maintainers)

The three authoring packages (`UttArena.Contracts`, `UttArena.GameEngine`,
`UttArena.BotSdk.CSharp`) share `<UttArenaSdkVersion>` in `Directory.Build.props`.

### nuget.org Trusted Publishing (recommended)

CI publishes via GitHub Actions OIDC — no long-lived NuGet API key.

1. On [nuget.org Trusted Publishing](https://www.nuget.org/account/TrustedPublishing),
   create a policy:
   - **Repository Owner:** `coltonspears`
   - **Repository:** `UltimateTicTacToeArena`
   - **Workflow File:** `publish-bot-sdk.yml` (filename only)
   - **Environment:** leave blank (this workflow does not use one)
2. Bump `UttArenaSdkVersion` and update sample `PackageReference` versions.
3. Merge the workflow to `main`, then tag and push:
   ```bash
   git tag sdk-v1.2.0
   git push origin sdk-v1.2.0
   ```
   Or run **Publish Bot SDK** from the Actions tab.
4. Rebuild/redeploy the arena `bot-builder-csharp` image so prod uploads restore
   the same version offline. nuget.org does not help the sandboxed builder.

Because this repo is private, a new Trusted Publishing policy stays active for
**7 days** until the first successful `NuGet/login` exchange; after that it binds
permanently. Re-activate on nuget.org if you miss the window.

Leave nuget.org Trusted Publishing on this private repository until
[uttarena-bot-sdk](https://github.com/coltonspears/uttarena-bot-sdk) exists;
then add a second policy for the public repo. Do not move publishing in the
same change as opening the public tree.

Local CLI pushes still need a short-lived API key:

```powershell
$env:NUGET_API_KEY = '<key>'   # https://www.nuget.org/account/apikeys
./scripts/publish-bot-sdk.ps1
```

Pack-only smoke test (no upload): `./scripts/publish-bot-sdk.ps1 -SkipPush`

## Troubleshooting

**`NU1100: Unable to resolve 'UttArena.BotSdk.CSharp'`** — the offline feed is
empty or your `NuGet.config` does not point at it. Run
`scripts/pack-bot-sdk.ps1` (or `.sh`) and confirm `./.packages` contains three
`.nupkg` files. If the error mentions `PackageSourceMapping is enabled`, your
`NuGet.config` is missing the `<packageSourceMapping><clear /></packageSourceMapping>`
block.

**Build fails with "no project found"** — the `.csproj` is deeper than three
levels in the zip, usually because the folder itself was zipped rather than its
contents.

**Match ends immediately with a malformed-output error** — something other than a
protocol message reached stdout. `Console.WriteLine` in bot code is the usual
culprit; use `BotConsoleLog` instead.

**Bot forfeits on time in the arena but not locally** — the container gets a
smaller CPU share than your machine. Re-run the harness with
`--move-timeout-ms` set to half the arena budget; if it still overruns, reduce
your search's time fraction.
