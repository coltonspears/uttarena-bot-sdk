# Game modes

A mode is what a player picks before a game: how much time each side gets, and
which rules the board follows. Everything downstream — the clock, the rating pool
the result settles into, whether bots may play, and the ruleset hash a replay is
verified against — is derived from it, so the mode is the single knob rather than
a scattering of independent switches.

- [The seeded modes](#the-seeded-modes)
- [Clocks](#clocks)
- [Variants](#variants)
- [Rating pools](#rating-pools)
- [Choosing a mode through the API](#choosing-a-mode-through-the-api)
- [Adding a mode](#adding-a-mode)

## The seeded modes

`ArenaDefaults.ModePresets` is the source of truth; the arena seeds these on
startup and `GET /api/game-modes` returns them ordered for display.

| Key | Name | Time class | Variant | Clock | Ranked | Bots |
| --- | --- | --- | --- | --- | --- | --- |
| `bullet` | Bullet 1+0 | Bullet | Standard | 1 min, no increment | yes | yes |
| `blitz` | Blitz 3+2 | Blitz | Standard | 3 min + 2 s/move | yes | yes |
| `rapid` | Rapid 10+5 | Rapid | Standard | 10 min + 5 s/move | yes | yes |
| `classical` | Classical 30+0 | Classical | Standard | 30 min, no increment | yes | yes |
| `unlimited` | Unlimited | Unlimited | Standard | none | yes | yes |
| `chaos-blitz` | Chaos Blitz 3+2 | Blitz | Chaos | 3 min + 2 s/move | no | yes |
| `anarchy-bullet` | Anarchy Bullet 1+0 | Bullet | Anarchy | 1 min, no increment | no | yes |
| `wildcard-blitz` | Wildcard Blitz 3+2 | Blitz | Wildcard | 3 min + 2 s/move | yes | yes |
| `sudden-death-blitz` | Sudden Death Blitz 3+2 | Blitz | Sudden Death | 3 min + 2 s/move | no | yes |
| `territory-rapid` | Territory Rapid 10+5 | Rapid | Territory | 10 min + 5 s/move | no | yes |
| `misere-blitz` | Misère Blitz 3+2 | Blitz | Misère | 3 min + 2 s/move | no | yes |
| `center-control-blitz` | Center Control Blitz 3+2 | Blitz | Center Control | 3 min + 2 s/move | no | yes |
| `locked-arena-blitz` | Locked Arena Blitz 3+2 | Blitz | Locked Arena | 3 min + 2 s/move | yes | yes |
| `double-move-rapid` | Double Move Rapid 10+5 | Rapid | Double Move | 10 min + 5 s/move | no | yes |
| `draft-opening-blitz` | Draft Opening Blitz 3+2 | Blitz | Draft Opening | 3 min + 2 s/move | yes | no |
| `relay-blitz` | Relay Blitz 3+2 | Blitz | Relay | 3 min + 2 s/move | no | no |
| `last-stand-blitz` | Last Stand Blitz 3+2 | Blitz | Last Stand | 3 min + 2 s/move | no | no |

`unlimited` is the default. A request that names no mode gets it, which keeps
every caller written before modes existed on the untimed rules it was built
against.

Each mode owns a seeded `Ruleset` whose definition pins the timing and the
variant, and every match stores that ruleset's sha256. A replay is therefore
verified against the rules it was actually played under, even after a preset is
edited.

## Clocks

The server is the only authority on time. Each seat starts with the mode's
`initialBankMs`, and `MatchEntity.TurnStartedAtUtc` records when the current turn
began. On every move, inside the per-match lock:

1. The mover's elapsed time is measured against `TurnStartedAtUtc`.
2. If it exceeds their remaining bank, the move is rejected and the match is
   completed as a `Timeout` loss. A player cannot buy a lost position back by
   moving late.
3. Otherwise the elapsed time is deducted, `incrementMs` is added, and the turn
   clock restarts.

`MatchClockService` sweeps running human matches once a second and flags a seat
whose bank has run out without waiting for a move, so a player who walks away
actually loses. It goes through `IMatchCoordinator.EnforceClockAsync`, taking the
same lock a move does.

Clients receive a `MatchClockSnapshot` (`xMs`, `oMs`, `turnStartedAtUtc`,
`incrementMs`, `initialBankMs`) on every move and in `GET /api/matches/{id}/state`,
and run the countdown locally between updates rather than polling. Bot seats are
timed inside the runner, which owns the container for the whole turn.

## Variants

**Chaos** draws the next forced board from the boards still in play, using a
deterministic PRNG seeded from `(MatchEntity.RandomSeed, ply)`.

**Anarchy** never forces a board.

**Wildcard** gives each player one optional move that ignores the forced board.
The client must explicitly activate the wildcard for that move.

**Sudden Death** ends as soon as one player controls any three local boards.

**Territory** continues until no local boards remain playable. A controlled
board is worth one point and each distinct macro winning line is worth one bonus
point; the higher score wins.

**Misère** makes the player who completes a macro winning line lose.

**Center Control** also continues until no boards remain playable. Controlled
boards are worth one point, except the center board, which is worth two.

**Locked Arena** advances a closed destination clockwise to the next playable
board instead of granting free choice.

**Double Move** gives each player one optional two-move turn. The first move
routes the second and the second routes the opponent.

**Draft Opening** starts with four neutral blocked-cell placements in X-O-X-O
order. The clock starts only after the draft; normal play then begins with X.

**Relay** swaps the symbols controlled by the two players after every won local
board. Captured-board ownership and each player's clock stay with that player.

**Last Stand** gives the opponent one normally routed response after the first
macro line. A counter-line wins; any other response confirms the original win.

Live clients receive all rule-relevant state from the server. State hashing
includes special availability, draft progress, relay ownership/symbol mapping,
and pending multi-move or Last Stand phases so reconnects and replays remain
deterministic.

Wildcard, Double Move, Chaos, Anarchy, Locked Arena, Sudden Death, Territory,
Misère, and Center Control are bot-eligible. `legalMoves` is always the complete
legal set, so existing bots play those modes legally. SDK 1.2 bots also receive
the real variant name (and Chaos `randomSeed`) so search can match the engine.
Draft Opening, Relay, and Last Stand stay human-only until the protocol grows
extra message types.

Bot authors may restrict a bot to a subset of modes via `supportedModeKeys`.
Owners may still start an **unlisted** debug match (`"unlisted": true`) for any
protocol-supported variant.

## Rating pools

Ratings are held per time class in `RatingPool`, keyed by
`(participantKind, participantId, timeClass)`. A bullet result never moves a
classical rating. `HumanRatingHistory` and `BotRatingHistory` also record the
time class, and `GET /api/leaderboards/humans?timeClass=blitz` reads the matching
pool.

A participant's first game in a pool opens at whatever single rating they carried
before pools existed — `ApplicationUser.HumanRating` or `BotVersion.Rating` —
rather than resetting to 1200. Those two columns are kept as mirrors of the most
recent settlement so existing leaderboard queries keep working.

`ArenaRatings` in `UttArena.Infrastructure` is the only implementation of this.
The API uses it when a human move ends a match and the runner uses it when a bot
game ends inside a worker, so the two paths cannot drift.

## Choosing a mode through the API

Every creation endpoint accepts either `gameModeId` or `gameModeKey`:

```bash
# A public ranked blitz room
curl -X POST http://localhost:5258/api/rooms \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"gameModeKey":"blitz","isRanked":true,"isPublic":true,"hostMark":"RANDOM"}'

# Pair into the oldest waiting rapid room, or open one
curl -X POST http://localhost:5258/api/matchmaking/quick \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"gameModeKey":"rapid","isRanked":false}'

# Challenge a public bot at bullet
curl -X POST http://localhost:5258/api/matches/human-vs-bot \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"botVersionId":"...","humanMark":"X","gameModeKey":"bullet"}'
```

`GET /api/rooms/public?timeClass=blitz&variant=standard&ranked=true` is the room
browser's query, and `GET /api/bots/arena?timeClass=blitz&difficulty=advanced`
is the bot arena's.

## Adding a mode

Add a `GameModePreset` to `ArenaDefaults.ModePresets`. On the next start the
seeder creates the mode and its ruleset; existing modes are left alone, so the
list is safe to extend. A new variant additionally needs matching engine and
infrastructure `GameVariant` members, deterministic state serialization and
hashing, coordinator handling, web rendering/actions, and rule-focused tests.
