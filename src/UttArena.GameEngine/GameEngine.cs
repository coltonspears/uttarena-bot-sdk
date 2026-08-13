using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace UttArena.GameEngine;

public enum Cell : byte
{
    Empty = 0,
    X = 1,
    O = 2,
    Blocked = 3
}

public enum BoardResult : byte
{
    InProgress = 0,
    XWin = 1,
    OWin = 2,
    Draw = 3
}

public enum GameVariant : byte
{
    /// <summary>The played cell index selects the next board.</summary>
    Standard = 1,

    /// <summary>The next board is drawn from the open boards, seeded per ply.</summary>
    Chaos = 2,

    /// <summary>There is never a forced board.</summary>
    Anarchy = 3,
    Wildcard = 4,
    SuddenDeath = 5,
    Territory = 6,
    Misere = 7,
    CenterControl = 8,
    LockedArena = 9,
    DoubleMove = 10,
    DraftOpening = 11,
    Relay = 12,
    LastStand = 13
}

public readonly record struct Move
{
    public Move(int board, int cell)
    {
        if (board is < 0 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(board), "Board must be between 0 and 8.");
        }

        if (cell is < 0 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(cell), "Cell must be between 0 and 8.");
        }

        Board = board;
        Cell = cell;
    }

    public int Board { get; }
    public int Cell { get; }
}

public sealed class GameState : IEquatable<GameState>
{
    private readonly Cell[] _cells;
    private readonly BoardResult[] _subBoardResults;
    private readonly ReadOnlyCollection<Cell> _cellsView;
    private readonly ReadOnlyCollection<BoardResult> _subBoardResultsView;

    private GameState(
        Cell[] cells,
        Cell nextPlayer,
        int? forcedBoard,
        int ply,
        GameVariant variant,
        BoardResult[]? subBoardResults,
        bool xSpecialAvailable,
        bool oSpecialAvailable,
        bool doubleMovePending,
        int draftMovesRemaining,
        Cell xPlayerSymbol,
        Cell oPlayerSymbol,
        Cell? lastStandPendingOwner,
        BoardResult? resultOverride)
    {
        _cells = (Cell[])cells.Clone();
        _subBoardResults = subBoardResults is null
            ? Enumerable.Range(0, 9)
                .Select(board => UltimateTicTacToe.EvaluateSubBoard(_cells, board))
                .ToArray()
            : (BoardResult[])subBoardResults.Clone();

        NextPlayer = nextPlayer;
        Variant = variant;
        ForcedBoard = draftMovesRemaining > 0
            ? null
            : UltimateTicTacToe.NormalizeForcedBoard(_subBoardResults, forcedBoard, variant);
        Ply = ply;
        XSpecialAvailable = xSpecialAvailable;
        OSpecialAvailable = oSpecialAvailable;
        DoubleMovePending = doubleMovePending;
        DraftMovesRemaining = draftMovesRemaining;
        XPlayerSymbol = xPlayerSymbol;
        OPlayerSymbol = oPlayerSymbol;
        LastStandPendingOwner = lastStandPendingOwner;
        ResultOverride = resultOverride;
        Result = resultOverride
            ?? UltimateTicTacToe.EvaluateGameResult(
                _subBoardResults,
                variant,
                draftMovesRemaining,
                lastStandPendingOwner);
        _cellsView = Array.AsReadOnly(_cells);
        _subBoardResultsView = Array.AsReadOnly(_subBoardResults);
    }

    public IReadOnlyList<Cell> Cells => _cellsView;
    public IReadOnlyList<BoardResult> SubBoardResults => _subBoardResultsView;
    public GameVariant Variant { get; }
    public Cell NextPlayer { get; }
    public Cell CurrentSymbol => NextPlayer == Cell.X ? XPlayerSymbol : OPlayerSymbol;
    public int? ForcedBoard { get; }
    public int Ply { get; }
    public BoardResult Result { get; }
    public bool IsTerminal => Result != BoardResult.InProgress;
    public bool XSpecialAvailable { get; }
    public bool OSpecialAvailable { get; }
    public bool DoubleMovePending { get; }
    public int DraftMovesRemaining { get; }
    public Cell XPlayerSymbol { get; }
    public Cell OPlayerSymbol { get; }
    public Cell? LastStandPendingOwner { get; }
    public BoardResult? ResultOverride { get; }
    public int XScore => UltimateTicTacToe.Score(_subBoardResults, Cell.X, Variant);
    public int OScore => UltimateTicTacToe.Score(_subBoardResults, Cell.O, Variant);

    public bool IsSpecialAvailable(Cell player)
    {
        ValidatePlayer(player, nameof(player));
        return player == Cell.X ? XSpecialAvailable : OSpecialAvailable;
    }

    public Cell GetCell(int board, int cell)
    {
        ValidateIndex(board, nameof(board));
        ValidateIndex(cell, nameof(cell));
        return _cells[(board * 9) + cell];
    }

    public BoardResult GetSubBoardResult(int board)
    {
        ValidateIndex(board, nameof(board));
        return _subBoardResults[board];
    }

    public static GameState Initial() => Initial(GameVariant.Standard);

    public static GameState Initial(GameVariant variant)
    {
        ValidateVariant(variant);
        return new GameState(
            new Cell[81],
            Cell.X,
            null,
            0,
            variant,
            null,
            xSpecialAvailable: true,
            oSpecialAvailable: true,
            doubleMovePending: false,
            draftMovesRemaining: variant == GameVariant.DraftOpening ? 4 : 0,
            xPlayerSymbol: Cell.X,
            oPlayerSymbol: Cell.O,
            lastStandPendingOwner: null,
            resultOverride: null);
    }

    public static GameState Create(
        IEnumerable<Cell> cells,
        Cell nextPlayer,
        int? forcedBoard = null,
        int ply = 0,
        GameVariant variant = GameVariant.Standard,
        IEnumerable<BoardResult>? subBoardResults = null,
        bool xSpecialAvailable = true,
        bool oSpecialAvailable = true,
        bool doubleMovePending = false,
        int? draftMovesRemaining = null,
        Cell xPlayerSymbol = Cell.X,
        Cell oPlayerSymbol = Cell.O,
        Cell? lastStandPendingOwner = null,
        BoardResult? resultOverride = null)
    {
        ArgumentNullException.ThrowIfNull(cells);
        var materialized = cells.ToArray();
        if (materialized.Length != 81)
        {
            throw new ArgumentException("Exactly 81 cells are required.", nameof(cells));
        }

        if (materialized.Any(cell => cell is < Cell.Empty or > Cell.Blocked))
        {
            throw new ArgumentException("Cells contain an unknown value.", nameof(cells));
        }

        ValidatePlayer(nextPlayer, nameof(nextPlayer));
        ValidateVariant(variant);

        if (forcedBoard is < 0 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(forcedBoard), "Forced board must be between 0 and 8.");
        }

        if (ply < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ply));
        }

        var remaining = draftMovesRemaining
            ?? (variant == GameVariant.DraftOpening ? Math.Max(0, 4 - ply) : 0);
        if (remaining is < 0 or > 4)
        {
            throw new ArgumentOutOfRangeException(
                nameof(draftMovesRemaining),
                "Draft moves remaining must be between 0 and 4.");
        }

        if (doubleMovePending && variant != GameVariant.DoubleMove)
        {
            throw new ArgumentException(
                "A pending double move is only valid for DoubleMove.",
                nameof(doubleMovePending));
        }

        ValidateSymbol(xPlayerSymbol, nameof(xPlayerSymbol));
        ValidateSymbol(oPlayerSymbol, nameof(oPlayerSymbol));
        if (xPlayerSymbol == oPlayerSymbol)
        {
            throw new ArgumentException("Player symbols must be different.");
        }

        if (lastStandPendingOwner is Cell pendingOwner)
        {
            ValidatePlayer(pendingOwner, nameof(lastStandPendingOwner));
            if (variant != GameVariant.LastStand)
            {
                throw new ArgumentException(
                    "A pending response is only valid for LastStand.",
                    nameof(lastStandPendingOwner));
            }
        }

        if (resultOverride is BoardResult.InProgress)
        {
            throw new ArgumentException(
                "An explicit result must be terminal.",
                nameof(resultOverride));
        }

        BoardResult[]? materializedResults = null;
        if (subBoardResults is not null)
        {
            materializedResults = subBoardResults.ToArray();
            if (materializedResults.Length != 9)
            {
                throw new ArgumentException(
                    "Exactly nine sub-board results are required.",
                    nameof(subBoardResults));
            }

            if (materializedResults.Any(result => result is < BoardResult.InProgress or > BoardResult.Draw))
            {
                throw new ArgumentException(
                    "Sub-board results contain an unknown value.",
                    nameof(subBoardResults));
            }
        }

        return new GameState(
            materialized,
            nextPlayer,
            forcedBoard,
            ply,
            variant,
            materializedResults,
            xSpecialAvailable,
            oSpecialAvailable,
            doubleMovePending,
            remaining,
            xPlayerSymbol,
            oPlayerSymbol,
            lastStandPendingOwner,
            resultOverride);
    }

    internal Cell[] CopyCells() => (Cell[])_cells.Clone();
    internal BoardResult[] CopySubBoardResults() => (BoardResult[])_subBoardResults.Clone();

    public bool Equals(GameState? other) =>
        other is not null
        && Variant == other.Variant
        && NextPlayer == other.NextPlayer
        && ForcedBoard == other.ForcedBoard
        && Ply == other.Ply
        && XSpecialAvailable == other.XSpecialAvailable
        && OSpecialAvailable == other.OSpecialAvailable
        && DoubleMovePending == other.DoubleMovePending
        && DraftMovesRemaining == other.DraftMovesRemaining
        && XPlayerSymbol == other.XPlayerSymbol
        && OPlayerSymbol == other.OPlayerSymbol
        && LastStandPendingOwner == other.LastStandPendingOwner
        && ResultOverride == other.ResultOverride
        && _subBoardResults.SequenceEqual(other._subBoardResults)
        && _cells.SequenceEqual(other._cells);

    public override bool Equals(object? obj) => Equals(obj as GameState);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Variant);
        hash.Add(NextPlayer);
        hash.Add(ForcedBoard);
        hash.Add(Ply);
        hash.Add(XSpecialAvailable);
        hash.Add(OSpecialAvailable);
        hash.Add(DoubleMovePending);
        hash.Add(DraftMovesRemaining);
        hash.Add(XPlayerSymbol);
        hash.Add(OPlayerSymbol);
        hash.Add(LastStandPendingOwner);
        hash.Add(ResultOverride);
        foreach (var cell in _cells)
        {
            hash.Add(cell);
        }

        foreach (var result in _subBoardResults)
        {
            hash.Add(result);
        }

        return hash.ToHashCode();
    }

    private static void ValidateIndex(int value, string name)
    {
        if (value is < 0 or > 8)
        {
            throw new ArgumentOutOfRangeException(name, "Index must be between 0 and 8.");
        }
    }

    private static void ValidatePlayer(Cell player, string name)
    {
        if (player is not (Cell.X or Cell.O))
        {
            throw new ArgumentOutOfRangeException(name, "Player must be X or O.");
        }
    }

    private static void ValidateSymbol(Cell symbol, string name)
    {
        if (symbol is not (Cell.X or Cell.O))
        {
            throw new ArgumentOutOfRangeException(name, "A player symbol must be X or O.");
        }
    }

    private static void ValidateVariant(GameVariant variant)
    {
        if (!Enum.IsDefined(variant))
        {
            throw new ArgumentOutOfRangeException(nameof(variant));
        }
    }
}

public static class UltimateTicTacToe
{
    private static readonly int[][] WinningLines =
    [
        [0, 1, 2], [3, 4, 5], [6, 7, 8],
        [0, 3, 6], [1, 4, 7], [2, 5, 8],
        [0, 4, 8], [2, 4, 6]
    ];

    public static IReadOnlyList<Move> GetLegalMoves(GameState state) =>
        GetLegalMoves(state, state?.Variant ?? GameVariant.Standard, useSpecial: false);

    /// <summary>
    /// When requested for Wildcard, returns the union of ordinary moves and moves
    /// reachable by spending the current player's wildcard.
    /// </summary>
    public static IReadOnlyList<Move> GetLegalMoves(GameState state, bool includeSpecial)
    {
        ArgumentNullException.ThrowIfNull(state);
        var ordinary = GetLegalMoves(state, state.Variant, useSpecial: false);
        if (!includeSpecial)
        {
            return ordinary;
        }

        return ordinary
            .Concat(GetLegalMoves(state, state.Variant, useSpecial: true))
            .Distinct()
            .OrderBy(move => move.Board)
            .ThenBy(move => move.Cell)
            .ToArray();
    }

    public static IReadOnlyList<Move> GetLegalMoves(
        GameState state,
        GameVariant variant,
        bool useSpecial = false)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.IsTerminal)
        {
            return Array.Empty<Move>();
        }

        var draftRemaining = EffectiveDraftMovesRemaining(state, variant);
        if (draftRemaining > 0)
        {
            if (useSpecial)
            {
                return Array.Empty<Move>();
            }

            return Enumerable.Range(0, 9)
                .SelectMany(board => Enumerable.Range(0, 9)
                    .Where(cell => state.GetCell(board, cell) == Cell.Empty)
                    .Select(cell => new Move(board, cell)))
                .ToArray();
        }

        IEnumerable<int> boards;
        if (useSpecial)
        {
            if (variant == GameVariant.Wildcard
                && state.IsSpecialAvailable(state.NextPlayer)
                && state.ForcedBoard is int forced)
            {
                boards = Enumerable.Range(0, 9)
                    .Where(board => board != forced
                        && state.GetSubBoardResult(board) == BoardResult.InProgress);
            }
            else if (variant == GameVariant.DoubleMove
                && !state.DoubleMovePending
                && state.IsSpecialAvailable(state.NextPlayer))
            {
                boards = OrdinaryBoards(state);
            }
            else
            {
                return Array.Empty<Move>();
            }
        }
        else
        {
            boards = OrdinaryBoards(state);
        }

        return boards
            .SelectMany(board => Enumerable.Range(0, 9)
                .Where(cell => state.GetCell(board, cell) == Cell.Empty)
                .Select(cell => new Move(board, cell)))
            .ToArray();
    }

    public static bool IsLegalMove(GameState state, Move move) =>
        IsLegalMove(state, move, state?.Variant ?? GameVariant.Standard, useSpecial: false);

    public static bool IsLegalMove(GameState state, Move move, bool useSpecial) =>
        IsLegalMove(state, move, state?.Variant ?? GameVariant.Standard, useSpecial);

    public static bool IsLegalMove(
        GameState state,
        Move move,
        GameVariant variant,
        bool useSpecial = false)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.IsTerminal || state.GetCell(move.Board, move.Cell) != Cell.Empty)
        {
            return false;
        }

        if (EffectiveDraftMovesRemaining(state, variant) > 0)
        {
            return !useSpecial;
        }

        if (state.GetSubBoardResult(move.Board) != BoardResult.InProgress)
        {
            return false;
        }

        if (!useSpecial)
        {
            return state.ForcedBoard is null || state.ForcedBoard == move.Board;
        }

        return variant switch
        {
            GameVariant.Wildcard =>
                state.IsSpecialAvailable(state.NextPlayer)
                && state.ForcedBoard is int forced
                && move.Board != forced,
            GameVariant.DoubleMove =>
                !state.DoubleMovePending
                && state.IsSpecialAvailable(state.NextPlayer)
                && (state.ForcedBoard is null || state.ForcedBoard == move.Board),
            _ => false
        };
    }

    public static GameState ApplyMove(GameState state, Move move) =>
        ApplyMove(state, move, state?.Variant ?? GameVariant.Standard, 0, useSpecial: false);

    public static GameState ApplyMove(GameState state, Move move, bool useSpecial) =>
        ApplyMove(state, move, state?.Variant ?? GameVariant.Standard, 0, useSpecial);

    public static GameState ApplyMove(
        GameState state,
        Move move,
        GameVariant variant,
        ulong seed) =>
        ApplyMove(state, move, variant, seed, useSpecial: false);

    /// <summary>
    /// Applies a move under a variant. The special flag spends a Wildcard or starts
    /// a DoubleMove only after the move has passed every legality check.
    /// </summary>
    public static GameState ApplyMove(
        GameState state,
        Move move,
        GameVariant variant,
        ulong seed,
        bool useSpecial)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.IsTerminal)
        {
            throw new InvalidOperationException("Cannot apply a move to a completed game.");
        }

        if (!IsLegalMove(state, move, variant, useSpecial))
        {
            throw new InvalidOperationException("The move is not legal in the current state.");
        }

        var draftRemaining = EffectiveDraftMovesRemaining(state, variant);
        var cells = state.CopyCells();
        var boardResults = state.CopySubBoardResults();
        var placedCell = draftRemaining > 0
            ? Cell.Blocked
            : SymbolForPlayer(state, state.NextPlayer, variant);
        cells[(move.Board * 9) + move.Cell] = placedCell;

        var previousBoardResult = boardResults[move.Board];
        var evaluatedBoard = EvaluateSubBoard(cells, move.Board);
        var boardWasWon = false;
        if (previousBoardResult == BoardResult.InProgress)
        {
            if (evaluatedBoard is BoardResult.XWin or BoardResult.OWin)
            {
                boardResults[move.Board] = variant == GameVariant.Relay
                    ? PlayerResult(state.NextPlayer)
                    : evaluatedBoard;
                boardWasWon = true;
            }
            else if (evaluatedBoard == BoardResult.Draw)
            {
                boardResults[move.Board] = BoardResult.Draw;
            }
        }

        var xSpecialAvailable = state.XSpecialAvailable;
        var oSpecialAvailable = state.OSpecialAvailable;
        if (useSpecial)
        {
            if (state.NextPlayer == Cell.X)
            {
                xSpecialAvailable = false;
            }
            else
            {
                oSpecialAvailable = false;
            }
        }

        var xSymbol = state.XPlayerSymbol;
        var oSymbol = state.OPlayerSymbol;
        if (variant == GameVariant.Relay && boardWasWon)
        {
            (xSymbol, oSymbol) = (oSymbol, xSymbol);
        }

        var nextPlayer = Opponent(state.NextPlayer);
        var doubleMovePending = false;
        if (variant == GameVariant.DoubleMove)
        {
            if (useSpecial)
            {
                nextPlayer = state.NextPlayer;
                doubleMovePending = true;
            }
            else if (state.DoubleMovePending)
            {
                nextPlayer = Opponent(state.NextPlayer);
            }
        }

        var nextDraftRemaining = Math.Max(0, draftRemaining - 1);
        var nextPly = state.Ply + 1;
        var forcedBoard = draftRemaining > 0
            ? null
            : NextForcedBoard(boardResults, move, variant, seed, nextPly);

        Cell? lastStandPendingOwner = null;
        BoardResult? resultOverride = null;
        if (variant == GameVariant.LastStand)
        {
            if (state.LastStandPendingOwner is Cell originalOwner)
            {
                resultOverride = HasMacroLine(boardResults, state.NextPlayer)
                    ? PlayerResult(state.NextPlayer)
                    : PlayerResult(originalOwner);
            }
            else if (!HasMacroLine(state.SubBoardResults, state.NextPlayer)
                && HasMacroLine(boardResults, state.NextPlayer))
            {
                if (boardResults.Any(result => result == BoardResult.InProgress))
                {
                    lastStandPendingOwner = state.NextPlayer;
                }
                else
                {
                    resultOverride = PlayerResult(state.NextPlayer);
                }
            }
        }

        return GameState.Create(
            cells,
            nextPlayer,
            forcedBoard,
            nextPly,
            variant,
            boardResults,
            xSpecialAvailable,
            oSpecialAvailable,
            doubleMovePending,
            nextDraftRemaining,
            xSymbol,
            oSymbol,
            lastStandPendingOwner,
            resultOverride);
    }

    /// <summary>
    /// Preserves the original routing helper for callers that only have cells.
    /// Relay callers should use state-based move application so stable ownership is
    /// not inferred from mark glyphs.
    /// </summary>
    public static int? NextForcedBoard(
        IReadOnlyList<Cell> cells,
        Move move,
        GameVariant variant,
        ulong seed,
        int ply)
    {
        ArgumentNullException.ThrowIfNull(cells);
        var results = Enumerable.Range(0, 9)
            .Select(board => EvaluateSubBoard(cells, board))
            .ToArray();
        return NextForcedBoard(results, move, variant, seed, ply);
    }

    internal static int? NormalizeForcedBoard(
        IReadOnlyList<BoardResult> boards,
        int? forcedBoard,
        GameVariant variant)
    {
        if (forcedBoard is not int target)
        {
            return null;
        }

        if (boards[target] == BoardResult.InProgress)
        {
            return target;
        }

        return variant == GameVariant.LockedArena
            ? NextOpenClockwise(boards, target)
            : null;
    }

    internal static BoardResult EvaluateSubBoard(IReadOnlyList<Cell> cells, int board)
    {
        var offset = board * 9;
        foreach (var line in WinningLines)
        {
            var first = cells[offset + line[0]];
            if (first is Cell.X or Cell.O
                && first == cells[offset + line[1]]
                && first == cells[offset + line[2]])
            {
                return first == Cell.X ? BoardResult.XWin : BoardResult.OWin;
            }
        }

        return Enumerable.Range(0, 9).All(cell => cells[offset + cell] != Cell.Empty)
            ? BoardResult.Draw
            : BoardResult.InProgress;
    }

    internal static BoardResult EvaluateMetaBoard(IReadOnlyList<BoardResult> boards)
    {
        foreach (var line in WinningLines)
        {
            var first = boards[line[0]];
            if (first is BoardResult.XWin or BoardResult.OWin
                && first == boards[line[1]]
                && first == boards[line[2]])
            {
                return first;
            }
        }

        return boards.All(result => result != BoardResult.InProgress)
            ? BoardResult.Draw
            : BoardResult.InProgress;
    }

    internal static BoardResult EvaluateGameResult(
        IReadOnlyList<BoardResult> boards,
        GameVariant variant,
        int draftMovesRemaining,
        Cell? lastStandPendingOwner)
    {
        if (draftMovesRemaining > 0 || lastStandPendingOwner is not null)
        {
            return BoardResult.InProgress;
        }

        if (variant == GameVariant.SuddenDeath)
        {
            if (boards.Count(result => result == BoardResult.XWin) >= 3)
            {
                return BoardResult.XWin;
            }

            if (boards.Count(result => result == BoardResult.OWin) >= 3)
            {
                return BoardResult.OWin;
            }

            return boards.All(result => result != BoardResult.InProgress)
                ? BoardResult.Draw
                : BoardResult.InProgress;
        }

        if (variant is GameVariant.Territory or GameVariant.CenterControl)
        {
            if (boards.Any(result => result == BoardResult.InProgress))
            {
                return BoardResult.InProgress;
            }

            var xScore = Score(boards, Cell.X, variant);
            var oScore = Score(boards, Cell.O, variant);
            return xScore == oScore
                ? BoardResult.Draw
                : xScore > oScore ? BoardResult.XWin : BoardResult.OWin;
        }

        var macro = EvaluateMetaBoard(boards);
        if (variant == GameVariant.Misere
            && macro is BoardResult.XWin or BoardResult.OWin)
        {
            return macro == BoardResult.XWin ? BoardResult.OWin : BoardResult.XWin;
        }

        return macro;
    }

    internal static int Score(
        IReadOnlyList<BoardResult> boards,
        Cell player,
        GameVariant variant)
    {
        var owned = PlayerResult(player);
        var score = 0;
        for (var board = 0; board < 9; board++)
        {
            if (boards[board] == owned)
            {
                score += variant == GameVariant.CenterControl && board == 4 ? 2 : 1;
            }
        }

        if (variant == GameVariant.Territory)
        {
            score += WinningLines.Count(line => line.All(board => boards[board] == owned));
        }

        return score;
    }

    private static IEnumerable<int> OrdinaryBoards(GameState state) =>
        state.ForcedBoard is int forced
            ? [forced]
            : Enumerable.Range(0, 9)
                .Where(board => state.GetSubBoardResult(board) == BoardResult.InProgress);

    private static int EffectiveDraftMovesRemaining(GameState state, GameVariant variant) =>
        variant == GameVariant.DraftOpening
            && state.Variant != GameVariant.DraftOpening
            && state.Ply == 0
                ? 4
                : state.DraftMovesRemaining;

    private static Cell SymbolForPlayer(GameState state, Cell player, GameVariant variant) =>
        variant == GameVariant.Relay
            ? (player == Cell.X ? state.XPlayerSymbol : state.OPlayerSymbol)
            : player;

    private static int? NextForcedBoard(
        IReadOnlyList<BoardResult> boards,
        Move move,
        GameVariant variant,
        ulong seed,
        int ply)
    {
        switch (variant)
        {
            case GameVariant.Anarchy:
                return null;

            case GameVariant.Chaos:
                var open = Enumerable.Range(0, 9)
                    .Where(board => boards[board] == BoardResult.InProgress)
                    .ToArray();
                return open.Length == 0
                    ? null
                    : open[new DeterministicPrng(ChaosSeed(seed, ply)).NextIndex(open.Length)];

            case GameVariant.LockedArena:
                return boards[move.Cell] == BoardResult.InProgress
                    ? move.Cell
                    : NextOpenClockwise(boards, move.Cell);

            default:
                return boards[move.Cell] == BoardResult.InProgress ? move.Cell : null;
        }
    }

    private static int? NextOpenClockwise(IReadOnlyList<BoardResult> boards, int target)
    {
        for (var offset = 1; offset < 9; offset++)
        {
            var board = (target + offset) % 9;
            if (boards[board] == BoardResult.InProgress)
            {
                return board;
            }
        }

        return null;
    }

    private static bool HasMacroLine(IReadOnlyList<BoardResult> boards, Cell player)
    {
        var owned = PlayerResult(player);
        return WinningLines.Any(line => line.All(board => boards[board] == owned));
    }

    private static BoardResult PlayerResult(Cell player) =>
        player == Cell.X ? BoardResult.XWin : BoardResult.OWin;

    private static Cell Opponent(Cell player) => player == Cell.X ? Cell.O : Cell.X;

    private static ulong ChaosSeed(ulong seed, int ply) =>
        unchecked((seed ^ 0xA24BAED4963EE407UL) + (0x9E3779B97F4A7C15UL * (ulong)ply));
}

public static class StateHasher
{
    public const string Format = "utt-state";
    public const int Version = 2;

    public static string Compute(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var canonical = new StringBuilder(128)
            .Append(Format).Append(':').Append(Version)
            .Append("|variant:").Append((byte)state.Variant)
            .Append("|next:").Append((byte)state.NextPlayer)
            .Append("|forced:")
                .Append(state.ForcedBoard?.ToString(CultureInfo.InvariantCulture) ?? "-")
            .Append("|ply:").Append(state.Ply)
            .Append("|special:")
                .Append(state.XSpecialAvailable ? '1' : '0')
                .Append(state.OSpecialAvailable ? '1' : '0')
            .Append("|double:").Append(state.DoubleMovePending ? '1' : '0')
            .Append("|draft:").Append(state.DraftMovesRemaining)
            .Append("|symbols:")
                .Append((byte)state.XPlayerSymbol)
                .Append((byte)state.OPlayerSymbol)
            .Append("|laststand:")
                .Append(state.LastStandPendingOwner is Cell pending
                    ? ((byte)pending).ToString(CultureInfo.InvariantCulture)
                    : "-")
            .Append("|override:")
                .Append(state.ResultOverride is BoardResult explicitResult
                    ? ((byte)explicitResult).ToString(CultureInfo.InvariantCulture)
                    : "-")
            .Append("|cells:");

        foreach (var cell in state.Cells)
        {
            canonical.Append((byte)cell);
        }

        canonical.Append("|boards:");
        foreach (var boardResult in state.SubBoardResults)
        {
            canonical.Append((byte)boardResult);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }
}
