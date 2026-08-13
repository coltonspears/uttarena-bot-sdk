using UttArena.Contracts;

namespace UttArena.GameEngine;

/// <summary>
/// Converts between the wire protocol's row/column board view and the engine's
/// board/cell indexing. The two layouts differ: the protocol lists the 81 cells
/// in row-major order across the whole grid, while the engine groups them by
/// local board. Getting this wrong produces legal-looking but wrong moves, so
/// bots and the arena share this one implementation.
/// </summary>
public static class ProtocolBridge
{
    public static Move ToMove(BoardPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        return new Move(
            (position.Row / 3 * 3) + (position.Column / 3),
            (position.Row % 3 * 3) + (position.Column % 3));
    }

    public static BoardPosition ToPosition(Move move) =>
        new(
            (move.Board / 3 * 3) + (move.Cell / 3),
            (move.Board % 3 * 3) + (move.Cell % 3));

    public static PlayerMark ToPlayerMark(Cell cell) =>
        cell == Cell.O ? PlayerMark.O : PlayerMark.X;

    public static Cell ToCell(PlayerMark mark) =>
        mark == PlayerMark.O ? Cell.O : Cell.X;

    /// <summary>
    /// Maps an engine variant onto the wire. When <paramref name="extended"/> is
    /// false, only Wildcard and Double Move are named so SDK 1.1 bots can still
    /// parse the request. Other variants fall back to Standard; <c>LegalMoves</c>
    /// remains the complete legal set.
    /// </summary>
    public static ProtocolGameVariant ToProtocolVariant(
        GameVariant variant,
        bool extended = true) =>
        variant switch
        {
            GameVariant.Wildcard => ProtocolGameVariant.Wildcard,
            GameVariant.DoubleMove => ProtocolGameVariant.DoubleMove,
            GameVariant.Chaos when extended => ProtocolGameVariant.Chaos,
            GameVariant.Anarchy when extended => ProtocolGameVariant.Anarchy,
            GameVariant.SuddenDeath when extended => ProtocolGameVariant.SuddenDeath,
            GameVariant.Territory when extended => ProtocolGameVariant.Territory,
            GameVariant.Misere when extended => ProtocolGameVariant.Misere,
            GameVariant.CenterControl when extended => ProtocolGameVariant.CenterControl,
            GameVariant.LockedArena when extended => ProtocolGameVariant.LockedArena,
            _ => ProtocolGameVariant.Standard,
        };

    public static GameVariant ToGameVariant(ProtocolGameVariant variant) => variant switch
    {
        ProtocolGameVariant.Wildcard => GameVariant.Wildcard,
        ProtocolGameVariant.DoubleMove => GameVariant.DoubleMove,
        ProtocolGameVariant.Chaos => GameVariant.Chaos,
        ProtocolGameVariant.Anarchy => GameVariant.Anarchy,
        ProtocolGameVariant.SuddenDeath => GameVariant.SuddenDeath,
        ProtocolGameVariant.Territory => GameVariant.Territory,
        ProtocolGameVariant.Misere => GameVariant.Misere,
        ProtocolGameVariant.CenterControl => GameVariant.CenterControl,
        ProtocolGameVariant.LockedArena => GameVariant.LockedArena,
        _ => GameVariant.Standard,
    };

    public static SpecialAvailability ToSpecialAvailability(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new SpecialAvailability(state.XSpecialAvailable, state.OSpecialAvailable);
    }

    public static BoardState ToBoardState(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var cells = new CellState[ProtocolConstants.CellCount];
        for (var board = 0; board < 9; board++)
        {
            for (var cell = 0; cell < 9; cell++)
            {
                var position = ToPosition(new Move(board, cell));
                cells[(position.Row * ProtocolConstants.BoardSize) + position.Column] =
                    state.GetCell(board, cell) switch
                    {
                        Cell.X => CellState.X,
                        Cell.O => CellState.O,
                        _ => CellState.Empty
                    };
            }
        }

        var localBoards = new LocalBoardState[9];
        for (var board = 0; board < 9; board++)
        {
            localBoards[board] = new LocalBoardState(board, state.GetSubBoardResult(board) switch
            {
                BoardResult.XWin => LocalBoardStatus.WonByX,
                BoardResult.OWin => LocalBoardStatus.WonByO,
                BoardResult.Draw => LocalBoardStatus.Draw,
                _ => LocalBoardStatus.Open
            });
        }

        return new BoardState(
            cells,
            localBoards,
            state.ForcedBoard,
            ToPlayerMark(state.NextPlayer),
            state.Ply);
    }

    public static GameState ToGameState(BoardState board)
    {
        ArgumentNullException.ThrowIfNull(board);
        if (board.Cells is null || board.Cells.Count != ProtocolConstants.CellCount)
        {
            throw new ArgumentException(
                $"A board must contain exactly {ProtocolConstants.CellCount} cells.",
                nameof(board));
        }

        var cells = new Cell[ProtocolConstants.CellCount];
        for (var row = 0; row < ProtocolConstants.BoardSize; row++)
        {
            for (var column = 0; column < ProtocolConstants.BoardSize; column++)
            {
                var move = ToMove(new BoardPosition(row, column));
                cells[(move.Board * 9) + move.Cell] =
                    board.Cells[(row * ProtocolConstants.BoardSize) + column] switch
                    {
                        CellState.X => Cell.X,
                        CellState.O => Cell.O,
                        _ => Cell.Empty
                    };
            }
        }

        return GameState.Create(
            cells,
            ToCell(board.CurrentTurn),
            board.ActiveLocalBoard,
            board.MoveNumber);
    }

    public static GameState ToGameState(MoveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var boardState = ToGameState(request.Board);
        var variant = ToGameVariant(request.Variant);
        var availability = request.SpecialAvailability;
        return GameState.Create(
            boardState.Cells,
            boardState.NextPlayer,
            boardState.ForcedBoard,
            boardState.Ply,
            variant,
            boardState.SubBoardResults,
            xSpecialAvailable: availability?.X ?? true,
            oSpecialAvailable: availability?.O ?? true,
            doubleMovePending: request.DoubleMovePending);
    }
}
