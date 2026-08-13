# UttArena.GameEngine

The canonical Ultimate Tic-Tac-Toe rules engine used by the arena to adjudicate
every match. Bots that search ahead use it to apply candidate moves and evaluate
positions with exactly the same rules the arena enforces.

```csharp
using UttArena.GameEngine;

var state = ProtocolBridge.ToGameState(request);
foreach (var move in UltimateTicTacToe.GetLegalMoves(state))
{
    var next = UltimateTicTacToe.ApplyMove(state, move, state.Variant, request.RandomSeed ?? 0);
    // score `next`
}
```

`ProtocolBridge` converts between the protocol's row/column positions and the
engine's board/cell indexing. Use it rather than rolling your own mapping: the
protocol lists cells in row-major order across the whole 9x9 grid, while the
engine groups them by local board.

`ToGameState(MoveRequest)` preserves the variant, special availability, and
pending Double Move state. Chaos search needs `request.RandomSeed`, which the
arena sends only to SDK 1.2+ bots.

Add this package next to `UttArena.BotSdk.CSharp` when you want to look ahead.
Author docs: `/docs/bots` on the arena site.
