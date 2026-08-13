using UttArena.Contracts;
using UttArena.GameEngine;

namespace UttArena.GameEngine.Tests;

public sealed class ProtocolBridgeTests
{
    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(0, 3, 1, 0)] // engine board0/cell3 ≠ spatial (0,3); maps to row1 col0
    [InlineData(1, 0, 0, 3)]
    [InlineData(7, 2, 6, 5)] // UI "Board 8" (bottom middle), 3rd cell
    [InlineData(8, 8, 8, 8)]
    public void ToPositionMapsBoardCellToSpatialRowColumn(int board, int cell, int row, int column)
    {
        var position = ProtocolBridge.ToPosition(new Move(board, cell));

        Assert.Equal(row, position.Row);
        Assert.Equal(column, position.Column);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(1, 0, 0, 3)]
    [InlineData(0, 3, 1, 0)]
    [InlineData(6, 5, 7, 2)]
    [InlineData(8, 8, 8, 8)]
    public void ToMoveMapsSpatialRowColumnToBoardCell(int row, int column, int board, int cell)
    {
        var move = ProtocolBridge.ToMove(new BoardPosition(row, column));

        Assert.Equal(board, move.Board);
        Assert.Equal(cell, move.Cell);
    }

    [Fact]
    public void BoardCellRoundTripCoversEverySquare()
    {
        for (var board = 0; board < 9; board++)
        {
            for (var cell = 0; cell < 9; cell++)
            {
                var position = ProtocolBridge.ToPosition(new Move(board, cell));
                var roundTrip = ProtocolBridge.ToMove(position);

                Assert.Equal(board, roundTrip.Board);
                Assert.Equal(cell, roundTrip.Cell);
            }
        }
    }

    [Fact]
    public void ToBoardStatePlacesMarksInSpatialLayoutNotEngineLayout()
    {
        // Same mismatch the web client hit: engine index 65 vs spatial index 59.
        var cells = new Cell[81];
        cells[(7 * 9) + 2] = Cell.X;
        var state = GameState.Create(cells, Cell.O, forcedBoard: 2, ply: 1);

        var board = ProtocolBridge.ToBoardState(state);

        Assert.Equal(CellState.X, board.Cells[(6 * 9) + 5]);
        Assert.Equal(CellState.Empty, board.Cells[(7 * 9) + 2]);
        Assert.Equal(2, board.ActiveLocalBoard);
        Assert.Equal(PlayerMark.O, board.CurrentTurn);
    }

    [Fact]
    public void ToGameStateRoundTripsThroughSpatialBoardState()
    {
        var cells = new Cell[81];
        cells[(7 * 9) + 2] = Cell.X;
        cells[(0 * 9) + 3] = Cell.O;
        var original = GameState.Create(cells, Cell.X, forcedBoard: 3, ply: 2);

        var restored = ProtocolBridge.ToGameState(ProtocolBridge.ToBoardState(original));

        Assert.Equal(Cell.X, restored.GetCell(7, 2));
        Assert.Equal(Cell.O, restored.GetCell(0, 3));
        Assert.Equal(Cell.Empty, restored.GetCell(1, 0));
        Assert.Equal(3, restored.ForcedBoard);
        Assert.Equal(2, restored.Ply);
        Assert.Equal(Cell.X, restored.NextPlayer);
    }

    [Fact]
    public void MoveRequestRoundTripsDoubleMoveProtocolState()
    {
        var pending = UltimateTicTacToe.ApplyMove(
            GameState.Initial(GameVariant.DoubleMove),
            new Move(4, 4),
            GameVariant.DoubleMove,
            seed: 0,
            useSpecial: true);
        var request = new MoveRequest(
            ProtocolConstants.Version,
            ProtocolConstants.MoveRequestType,
            "request-special",
            "match-special",
            PlayerMark.X,
            ProtocolBridge.ToBoardState(pending),
            [new MoveRecord(1, PlayerMark.X, new BoardPosition(4, 4))],
            UltimateTicTacToe.GetLegalMoves(pending)
                .Select(ProtocolBridge.ToPosition)
                .ToArray(),
            new OpponentMetadata("o", "Opponent", null),
            RulesetDescriptor.Standard,
            new MoveTiming(1000, null, 0, DateTimeOffset.UtcNow.AddSeconds(1)),
            ProtocolBridge.ToProtocolVariant(pending.Variant),
            ProtocolBridge.ToSpecialAvailability(pending),
            pending.DoubleMovePending);

        var restored = ProtocolBridge.ToGameState(
            ProtocolJson.DeserializeLine<MoveRequest>(ProtocolJson.SerializeLine(request)));

        Assert.Equal(GameVariant.DoubleMove, restored.Variant);
        Assert.True(restored.DoubleMovePending);
        Assert.False(restored.XSpecialAvailable);
        Assert.True(restored.OSpecialAvailable);
        Assert.Equal(Cell.X, restored.NextPlayer);
        Assert.Equal(pending.ForcedBoard, restored.ForcedBoard);
        Assert.Equal(pending.Cells, restored.Cells);
    }

    [Fact]
    public void ToProtocolVariantHidesExtendedModesFromLegacyBots()
    {
        Assert.Equal(ProtocolGameVariant.Chaos, ProtocolBridge.ToProtocolVariant(GameVariant.Chaos));
        Assert.Equal(ProtocolGameVariant.Standard, ProtocolBridge.ToProtocolVariant(GameVariant.Chaos, extended: false));
        Assert.Equal(ProtocolGameVariant.Wildcard, ProtocolBridge.ToProtocolVariant(GameVariant.Wildcard, extended: false));
        Assert.Equal(GameVariant.LockedArena, ProtocolBridge.ToGameVariant(ProtocolGameVariant.LockedArena));
    }
}
