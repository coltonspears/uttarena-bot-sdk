# UttArena.Contracts

Protocol message contracts for UTT Arena bots: `MoveRequest`, `MoveResponse`,
`MatchStartMessage`, `ProtocolError`, the board and ruleset models, and the
`ProtocolValidation` helpers used on both sides of the wire.

Serialize with `ProtocolJson`. The wire format is newline-delimited JSON over
stdin and stdout. Property names and string enums are case-sensitive. Unknown
properties, comments, trailing commas, and numeric enums are invalid.

Most bot authors depend on `UttArena.BotSdk.CSharp` instead, which references
this package and handles the stdio loop.

## Variants

SDK 1.1 understands `standard`, `wildcard`, and `doubleMove`. SDK 1.2 also
understands `chaos`, `anarchy`, `suddenDeath`, `territory`, `misere`,
`centerControl`, and `lockedArena`. The arena only sends the extra variant
strings (and optional `randomSeed` for Chaos) to bots built against 1.2 or
later, so existing 1.1 bots keep running.

`LegalMoves` is always the complete legal set for the current turn, so a bot
that ignores `variant` still plays legally.

## Spec

The full protocol lives in `PROTOCOL.md` in
[uttarena-bot-sdk](https://github.com/coltonspears/uttarena-bot-sdk). Author
docs: `/docs/bots` on the arena site.
