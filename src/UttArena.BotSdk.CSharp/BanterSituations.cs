using UttArena.Contracts;

namespace UttArena.BotSdk.CSharp;

/// <summary>
/// Structured situation flags derived from the current <see cref="MoveRequest"/>.
/// Detectors only; bots author their own banter lines when a flag fires.
/// </summary>
[Flags]
public enum BanterSituation
{
    None = 0,
    Opening = 1 << 0,
    Endgame = 1 << 1,
    FreeMove = 1 << 2,
    OpponentJustWonLocalBoard = 1 << 3,
    BotCanTakeLocalBoard = 1 << 4,
    BotHasImmediateLocalThreat = 1 << 5,
    OpponentHasImmediateLocalThreat = 1 << 6,
    OpponentLastMoveCreatedThreat = 1 << 7,
}

public static class BanterSituations
{
    private static readonly int[][] WinningLines =
    [
        [0, 1, 2], [3, 4, 5], [6, 7, 8],
        [0, 3, 6], [1, 4, 7], [2, 5, 8],
        [0, 4, 8], [2, 4, 6]
    ];

    public static BanterSituation Analyze(MoveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var situations = BanterSituation.None;
        var moveNumber = request.Board.MoveNumber;

        if (moveNumber <= 6)
        {
            situations |= BanterSituation.Opening;
        }

        if (moveNumber >= 54)
        {
            situations |= BanterSituation.Endgame;
        }

        if (request.Board.ActiveLocalBoard is null)
        {
            situations |= BanterSituation.FreeMove;
        }

        var botCell = request.BotMark == PlayerMark.X ? CellState.X : CellState.O;
        var opponentCell = botCell == CellState.X ? CellState.O : CellState.X;

        if (OpponentJustClosedLocalBoard(request, opponentCell))
        {
            situations |= BanterSituation.OpponentJustWonLocalBoard;
        }

        if (CanCompleteLocalBoard(request, botCell))
        {
            situations |= BanterSituation.BotCanTakeLocalBoard;
        }

        if (HasImmediateThreat(request, botCell))
        {
            situations |= BanterSituation.BotHasImmediateLocalThreat;
        }

        if (HasImmediateThreat(request, opponentCell))
        {
            situations |= BanterSituation.OpponentHasImmediateLocalThreat;
        }

        if (OpponentLastMoveCreatedThreat(request, opponentCell))
        {
            situations |= BanterSituation.OpponentLastMoveCreatedThreat;
        }

        return situations;
    }

    private static bool OpponentJustClosedLocalBoard(MoveRequest request, CellState opponentCell)
    {
        if (request.History.Count == 0)
        {
            return false;
        }

        var last = request.History[^1];
        if (last.Mark == request.BotMark)
        {
            return false;
        }

        var localIndex = LocalBoardIndex(last.Position);
        var status = request.Board.LocalBoards.FirstOrDefault(board => board.Index == localIndex)?.Status;
        return opponentCell == CellState.X
            ? status == LocalBoardStatus.WonByX
            : status == LocalBoardStatus.WonByO;
    }

    private static bool CanCompleteLocalBoard(MoveRequest request, CellState botCell)
    {
        foreach (var move in request.LegalMoves)
        {
            if (WouldCompleteLocalBoard(request.Board.Cells, move, botCell))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasImmediateThreat(MoveRequest request, CellState mark)
    {
        var boards = request.Board.ActiveLocalBoard is int forced
            ? new[] { forced }
            : Enumerable.Range(0, ProtocolConstants.LocalBoardCount).ToArray();

        foreach (var board in boards)
        {
            if (request.Board.LocalBoards.FirstOrDefault(x => x.Index == board)?.Status
                != LocalBoardStatus.Open)
            {
                continue;
            }

            if (CountThreatsOnBoard(request.Board.Cells, board, mark) > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool OpponentLastMoveCreatedThreat(MoveRequest request, CellState opponentCell)
    {
        if (request.History.Count == 0)
        {
            return false;
        }

        var last = request.History[^1];
        if (last.Mark == request.BotMark)
        {
            return false;
        }

        var board = LocalBoardIndex(last.Position);
        return CountThreatsOnBoard(request.Board.Cells, board, opponentCell) > 0;
    }

    private static bool WouldCompleteLocalBoard(
        IReadOnlyList<CellState> cells,
        BoardPosition move,
        CellState mark)
    {
        var board = LocalBoardIndex(move);
        var localCell = LocalCellIndex(move);
        Span<CellState> local = stackalloc CellState[9];
        for (var i = 0; i < 9; i++)
        {
            local[i] = CellAt(cells, board, i);
        }

        local[localCell] = mark;
        foreach (var line in WinningLines)
        {
            if (local[line[0]] == mark &&
                local[line[1]] == mark &&
                local[line[2]] == mark)
            {
                return true;
            }
        }

        return false;
    }

    private static int CountThreatsOnBoard(IReadOnlyList<CellState> cells, int board, CellState mark)
    {
        var threats = 0;
        foreach (var line in WinningLines)
        {
            var marks = 0;
            var empties = 0;
            for (var i = 0; i < 3; i++)
            {
                var cell = CellAt(cells, board, line[i]);
                if (cell == mark)
                {
                    marks++;
                }
                else if (cell == CellState.Empty)
                {
                    empties++;
                }
            }

            if (marks == 2 && empties == 1)
            {
                threats++;
            }
        }

        return threats;
    }

    private static CellState CellAt(IReadOnlyList<CellState> cells, int board, int localCell)
    {
        var row = (board / 3 * 3) + (localCell / 3);
        var column = (board % 3 * 3) + (localCell % 3);
        return cells[(row * ProtocolConstants.BoardSize) + column];
    }

    private static int LocalBoardIndex(BoardPosition position) =>
        (position.Row / ProtocolConstants.LocalBoardSize * ProtocolConstants.LocalBoardSize)
        + (position.Column / ProtocolConstants.LocalBoardSize);

    private static int LocalCellIndex(BoardPosition position) =>
        (position.Row % ProtocolConstants.LocalBoardSize * ProtocolConstants.LocalBoardSize)
        + (position.Column % ProtocolConstants.LocalBoardSize);
}
