# Ultimate Tic-Tac-Toe Arena Bot Protocol v1

Protocol v1 is a newline-delimited JSON (NDJSON) protocol between the arena and a bot process. The arena writes requests to the bot's standard input. The bot writes protocol responses to standard output. Each non-empty line is exactly one complete JSON object.

## Transport rules

- Encoding is UTF-8.
- `protocolVersion` is the integer `1` on every message.
- `type` is one of the exact strings documented below.
- Property names and string enum values are case-sensitive.
- Unknown properties, comments, trailing commas, numeric enum values, and multi-line JSON are invalid in v1.
- The bot must flush standard output after each response.
- Standard output is reserved for protocol messages. Diagnostics go to standard error.
- The arena sends one `move_request` at a time to a bot process. A bot sends either one matching `move_response` or one matching `error`.

All row, column, board, and ply indices are integers. Rows and columns are zero-based. Plies are one-based.

## Stable string values

Player marks:

- `x`
- `o`

Cell states:

- `empty`
- `x`
- `o`

Local-board statuses:

- `open`
- `wonByX`
- `wonByO`
- `draw`

Error codes:

- `malformedRequest`
- `unsupportedVersion`
- `unsupportedMessage`
- `validationFailed`
- `moveTimedOut`
- `botFailure`
- `illegalMove`
- `internalError`

Log levels:

- `trace`
- `debug`
- `information`
- `warning`
- `error`

Values are closed sets for protocol v1. Implementations must not infer values from C# enum names or ordinals.

Bot-visible variants:

- `standard`
- `wildcard`
- `doubleMove`

SDK 1.2 additionally understands:

- `chaos`
- `anarchy`
- `suddenDeath`
- `territory`
- `misere`
- `centerControl`
- `lockedArena`

The arena only sends the extra variant strings, and the optional `randomSeed`
field, to bots built against SDK 1.2 or later. SDK 1.1 hosts reject unknown
enum values and unknown properties. `legalMoves` is always the complete legal
set for the current turn, so a 1.1 bot can still play the newer modes.

## Common objects

### Position

```json
{"row":4,"column":7}
```

Both coordinates must be from `0` through `8`.

### Board

`board` contains:

- `cells`: exactly 81 cell-state strings in global row-major order. Cell `(row, column)` is at `cells[row * 9 + column]`.
- `localBoards`: exactly nine objects with `index` and `status`. Indices `0` through `8` occur once each in local-board row-major order.
- `activeLocalBoard`: the local-board index in which the next move must be played, or `null` when the player has a free move.
- `currentTurn`: the mark whose move is requested.
- `moveNumber`: the number of already-played moves, from `0` through `81`.

For global position `(row, column)`, its local-board index is `(row / 3) * 3 + (column / 3)` using integer division.

### Move history

Every history item has:

- `ply`: one-based move number.
- `mark`: the player that made the move. Standard and Wildcard alternate; a
  DoubleMove activation produces two consecutive entries for the activating player.
- `position`: the global position played.

`history` is the complete current-match history, ordered oldest to newest. Its count equals `board.moveNumber`, and every recorded mark must be present in `board.cells`.

### Ruleset

The v1 standard ruleset object is:

```json
{"id":"ultimate-tic-tac-toe","version":1,"boardSize":9,"localBoardSize":3,"sendMoveToLocalBoard":true,"freeMoveWhenTargetBoardClosed":true}
```

A move at global `(row, column)` sends the opponent to local board `(row % 3) * 3 + (column % 3)`. If that target board is already won or drawn, the opponent may play in any open local board. Three won local boards in a row, column, or diagonal win the match. A drawn local board belongs to neither player.

### Opponent metadata

`opponent` is immutable for a match and contains only:

- `botId`: stable arena bot identifier.
- `displayName`: human-readable bot name.
- `version`: optional submitted bot version, or `null`.

Bots must not use opponent metadata as a source of secrets or mutable match state.

### Timing

A match timing control contains:

- `moveTimeoutMs`: positive wall-clock limit for each move.
- `initialBankMs`: optional non-negative total time bank, or `null` when no bank is used.
- `incrementMs`: non-negative bank increment after a move.

A move request contains:

- `moveTimeoutMs`: positive limit for this invocation.
- `remainingBankMs`: optional non-negative bank remaining before the move.
- `incrementMs`: non-negative increment.
- `deadlineUtc`: an ISO 8601 timestamp with a zero UTC offset (`Z` or `+00:00`).

The effective deadline is the earlier of `deadlineUtc` and the instant `moveTimeoutMs` elapses after receipt. Bots should honor cancellation promptly.

## `match_start`

The arena may send `match_start` once before the first move. It allows SDK hosts to validate and log immutable match configuration. It does not require a response.

```json
{"protocolVersion":1,"type":"match_start","matchId":"match-42","botMark":"x","opponent":{"botId":"opponent-7","displayName":"Example Opponent","version":"2.1.0"},"ruleset":{"id":"ultimate-tic-tac-toe","version":1,"boardSize":9,"localBoardSize":3,"sendMoveToLocalBoard":true,"freeMoveWhenTargetBoardClosed":true},"timing":{"moveTimeoutMs":1000,"initialBankMs":30000,"incrementMs":100}}
```

Required properties are `protocolVersion`, `type`, `matchId`, `botMark`, `opponent`, `ruleset`, and `timing`.

## `move_request`

The arena sends `move_request` when the bot must select a move. It is self-contained: a bot does not need to retain state from earlier lines.

Required properties:

- `protocolVersion`: `1`.
- `type`: `move_request`.
- `requestId`: non-empty opaque identifier unique within the match.
- `matchId`: non-empty opaque match identifier.
- `botMark`: the bot's mark; it equals `board.currentTurn`.
- `board`: the complete current board.
- `history`: the complete current-match move history.
- `legalMoves`: every currently legal global position. It is non-empty, contains no duplicates, and points only to empty cells in open local boards.
- `opponent`: immutable opponent metadata.
- `ruleset`: the complete ruleset descriptor.
- `timing`: timing information for this request.

Consumers must choose only from `legalMoves`; they do not need to independently reconstruct legality.

Special variants add optional, backward-compatible properties:

- `variant`: `wildcard` or `doubleMove` on every SDK. SDK 1.2 also receives
  `chaos`, `anarchy`, `suddenDeath`, `territory`, `misere`, `centerControl`,
  and `lockedArena`. It is omitted for Standard and defaults to `standard`
  when absent. Older bots receive `standard` for the extra modes.
- `specialAvailability`: `{"x":true|false,"o":true|false}`, recording whether
  each player can still spend their once-per-game special.
- `doubleMovePending`: `true` only while the activating player owes the second
  move of a DoubleMove. It is omitted when false.
- `legalSpecialMoves`: positions legal only when the response sets
  `useSpecial:true`. It is omitted when the current player cannot activate a
  special. For Wildcard it contains open cells outside the forced board. For
  DoubleMove it obeys the same routing as `legalMoves`.
- `randomSeed`: unsigned 64-bit match seed, sent only to SDK 1.2+ bots, and
  only when the variant needs it (Chaos). Search bots pass it to
  `UltimateTicTacToe.ApplyMove`.

`legalMoves` always retains its original v1 meaning: ordinary moves with no
special spent. Existing bots can keep selecting from it and omit `useSpecial`;
they remain legal in Wildcard and DoubleMove games. While `doubleMovePending` is
true, `legalMoves` describes the required second move and `legalSpecialMoves` is
omitted.

## `move_response`

The bot responds with the selected legal global position and must echo `requestId` exactly. Set the optional `useSpecial` boolean to `true` to select from `legalSpecialMoves`; missing or `false` means an ordinary move from `legalMoves`. An optional `banter` string (at most 120 characters, no newlines) may accompany the move. The arena rate-limits banter (at most five accepted lines per match, at least three plies apart) and posts accepted lines to match chat. Invalid or over-limit banter is dropped; the move still counts.

```json
{"protocolVersion":1,"type":"move_response","requestId":"request-17","move":{"row":4,"column":7},"banter":"Nice board."}
```

For a special move:

```json
{"protocolVersion":1,"type":"move_response","requestId":"request-17","move":{"row":0,"column":0},"useSpecial":true}
```

Omit `banter` (or send `null`) when the bot has nothing to say. Omit
`useSpecial` for ordinary moves. The arena treats a missing, duplicate,
mismatched, late, or illegal response as a bot failure according to match policy.

## `error`

Protocol-aware hosts may write an error object to standard output when they cannot produce a move response:

```json
{"protocolVersion":1,"type":"error","requestId":"request-17","code":"moveTimedOut","message":"The bot did not return a move before the deadline.","fatal":false}
```

`requestId` is the related request identifier when it could be recovered, otherwise `null`. `message` is safe for display and must not contain secrets. `fatal` tells the arena whether the process can continue reading later lines. An error is a protocol response, not a diagnostic log.

## `log`

Structured diagnostics use the following object, written only to standard error:

```json
{"protocolVersion":1,"type":"log","timestampUtc":"2026-07-24T01:00:00Z","level":"information","message":"Match started.","requestId":null}
```

Optional `label` and `data` fields support object inspection in unlisted debug matches:

```json
{"protocolVersion":1,"type":"log","timestampUtc":"2026-07-24T01:00:00Z","level":"debug","message":"scoredMoves","requestId":"request-17","label":"scoredMoves","data":{"best":{"row":4,"column":4},"score":12}}
```

`timestampUtc` is an ISO 8601 timestamp with a zero UTC offset. `requestId` is optional context represented as a string or `null`. Newlines inside `message` are JSON-escaped, so every log event remains one physical line. The arena never interprets standard-error lines as moves. In unlisted owner matches, the arena also mirrors each outgoing `move_request` (including board state) into the Debug feed.

## C# SDK execution

Reference `UttArena.BotSdk.CSharp`, implement `IBot.GetMoveAsync` (returning `BotDecision`), and start `BotConsoleHost.RunAsync`. The host:

- reads and validates one NDJSON message per standard-input line;
- invokes the bot only for valid `move_request` messages;
- enforces cancellation and the effective move deadline;
- validates ordinary moves against `legalMoves` and special moves against
  `legalSpecialMoves`;
- writes only `move_response` or `error` objects to standard output (with optional banter);
- writes structured logs (and `InspectAsync` object dumps) to standard error; and
- continues safely after non-fatal malformed input.

See `samples/bot-random-csharp` for a runnable bot that chooses a random legal
move, and `docs/BOT_AUTHORING.md` for the path from a scaffold to a promoted bot.
