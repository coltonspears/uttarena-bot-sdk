using UttArena.GameEngine;

namespace UttArena.GameEngine.Tests;

public sealed class GameEngineTests
{
    public static TheoryData<int[]> WinningLines => new()
    {
        { new[] { 0, 1, 2 } },
        { new[] { 3, 4, 5 } },
        { new[] { 6, 7, 8 } },
        { new[] { 0, 3, 6 } },
        { new[] { 1, 4, 7 } },
        { new[] { 2, 5, 8 } },
        { new[] { 0, 4, 8 } },
        { new[] { 2, 4, 6 } }
    };

    [Fact]
    public void InitialStateHasEveryMoveAvailable()
    {
        var state = GameState.Initial();

        var moves = UltimateTicTacToe.GetLegalMoves(state);

        Assert.Equal(81, moves.Count);
        Assert.Equal(new Move(0, 0), moves[0]);
        Assert.Equal(new Move(8, 8), moves[^1]);
        Assert.Equal(Cell.X, state.NextPlayer);
        Assert.Null(state.ForcedBoard);
        Assert.Equal(BoardResult.InProgress, state.Result);
    }

    [Fact]
    public void AppliedMoveForcesOpponentIntoMatchingBoard()
    {
        var initial = GameState.Initial();

        var after = UltimateTicTacToe.ApplyMove(initial, new Move(4, 6));

        Assert.Equal(Cell.X, after.GetCell(4, 6));
        Assert.Equal(Cell.O, after.NextPlayer);
        Assert.Equal(6, after.ForcedBoard);
        Assert.Equal(9, UltimateTicTacToe.GetLegalMoves(after).Count);
        Assert.All(UltimateTicTacToe.GetLegalMoves(after), move => Assert.Equal(6, move.Board));
        Assert.Equal(Cell.Empty, initial.GetCell(4, 6));
        Assert.Null(initial.ForcedBoard);
    }

    [Fact]
    public void TargetingCompletedBoardRestoresFreeChoice()
    {
        var cells = EmptyCells();
        SetBoard(cells, 3, DrawBoard);
        var state = GameState.Create(cells, Cell.X, forcedBoard: 0);

        var after = UltimateTicTacToe.ApplyMove(state, new Move(0, 3));
        var legalMoves = UltimateTicTacToe.GetLegalMoves(after);

        Assert.Null(after.ForcedBoard);
        Assert.DoesNotContain(legalMoves, move => move.Board == 3);
        Assert.Contains(legalMoves, move => move.Board == 0);
        Assert.Contains(legalMoves, move => move.Board == 8);
        Assert.Equal(71, legalMoves.Count);
    }

    [Fact]
    public void RequestedForcedBoardIsNormalizedWhenAlreadyComplete()
    {
        var cells = EmptyCells();
        SetBoard(cells, 5, DrawBoard);

        var state = GameState.Create(cells, Cell.X, forcedBoard: 5);

        Assert.Null(state.ForcedBoard);
        Assert.Equal(72, UltimateTicTacToe.GetLegalMoves(state).Count);
    }

    [Theory]
    [MemberData(nameof(WinningLines))]
    public void EveryWinningLineCompletesSubBoardForBothPlayers(int[] line)
    {
        foreach (var player in new[] { Cell.X, Cell.O })
        {
            var cells = EmptyCells();
            foreach (var cell in line)
            {
                cells[cell] = player;
            }

            var state = GameState.Create(cells, Opponent(player));

            Assert.Equal(
                player == Cell.X ? BoardResult.XWin : BoardResult.OWin,
                state.GetSubBoardResult(0));
        }
    }

    [Fact]
    public void FullSubBoardWithoutLineIsDraw()
    {
        var cells = EmptyCells();
        SetBoard(cells, 0, DrawBoard);

        var state = GameState.Create(cells, Cell.X);

        Assert.Equal(BoardResult.Draw, state.GetSubBoardResult(0));
        Assert.DoesNotContain(
            UltimateTicTacToe.GetLegalMoves(state),
            move => move.Board == 0);
    }

    [Theory]
    [MemberData(nameof(WinningLines))]
    public void EveryWinningLineCompletesMetaBoardForBothPlayers(int[] line)
    {
        foreach (var player in new[] { Cell.X, Cell.O })
        {
            var cells = EmptyCells();
            foreach (var board in line)
            {
                SetBoard(cells, board, WinningBoard(player));
            }

            var state = GameState.Create(cells, Opponent(player));

            Assert.True(state.IsTerminal);
            Assert.Equal(
                player == Cell.X ? BoardResult.XWin : BoardResult.OWin,
                state.Result);
            Assert.Empty(UltimateTicTacToe.GetLegalMoves(state));
        }
    }

    [Fact]
    public void NineDrawnSubBoardsProduceMetaDraw()
    {
        var cells = EmptyCells();
        for (var board = 0; board < 9; board++)
        {
            SetBoard(cells, board, DrawBoard);
        }

        var state = GameState.Create(cells, Cell.X);

        Assert.True(state.IsTerminal);
        Assert.Equal(BoardResult.Draw, state.Result);
        Assert.Empty(UltimateTicTacToe.GetLegalMoves(state));
    }

    [Fact]
    public void MetaBoardRemainsInProgressWhileAnySubBoardIsPlayable()
    {
        var cells = EmptyCells();
        for (var board = 0; board < 8; board++)
        {
            SetBoard(cells, board, DrawBoard);
        }

        var state = GameState.Create(cells, Cell.X);

        Assert.False(state.IsTerminal);
        Assert.Equal(BoardResult.InProgress, state.Result);
        Assert.Equal(9, UltimateTicTacToe.GetLegalMoves(state).Count);
    }

    [Fact]
    public void IllegalMovesAreRejectedWithoutChangingState()
    {
        var initial = GameState.Initial();
        var forced = UltimateTicTacToe.ApplyMove(initial, new Move(0, 4));
        var occupied = UltimateTicTacToe.ApplyMove(forced, new Move(4, 0));

        Assert.Throws<InvalidOperationException>(
            () => UltimateTicTacToe.ApplyMove(forced, new Move(3, 0)));
        Assert.Throws<InvalidOperationException>(
            () => UltimateTicTacToe.ApplyMove(occupied, new Move(0, 4)));
        Assert.Equal(Cell.Empty, forced.GetCell(3, 0));
        Assert.Equal(Cell.X, forced.GetCell(0, 4));
    }

    [Fact]
    public void MovesCannotUseCompletedSubBoardOrTerminalGame()
    {
        var cells = EmptyCells();
        SetBoard(cells, 0, WinningBoard(Cell.X));
        var state = GameState.Create(cells, Cell.O);

        Assert.Throws<InvalidOperationException>(
            () => UltimateTicTacToe.ApplyMove(state, new Move(0, 8)));

        SetBoard(cells, 1, WinningBoard(Cell.X));
        SetBoard(cells, 2, WinningBoard(Cell.X));
        var terminal = GameState.Create(cells, Cell.O);

        Assert.Throws<InvalidOperationException>(
            () => UltimateTicTacToe.ApplyMove(terminal, new Move(8, 8)));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(9, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 9)]
    public void MoveRejectsOutOfRangeCoordinates(int board, int cell)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Move(board, cell));
    }

    [Fact]
    public void StateDefensivelyCopiesInputAndExposesReadOnlyViews()
    {
        var cells = EmptyCells();
        var state = GameState.Create(cells, Cell.X);

        cells[0] = Cell.O;

        Assert.Equal(Cell.Empty, state.GetCell(0, 0));
        Assert.IsAssignableFrom<IReadOnlyList<Cell>>(state.Cells);
        Assert.Throws<NotSupportedException>(
            () => ((IList<Cell>)state.Cells)[0] = Cell.X);
    }

    [Fact]
    public void SeededRandomSelectionIsRepeatableAndAlwaysLegal()
    {
        var first = new DeterministicPrng(42);
        var second = new DeterministicPrng(42);
        var state = GameState.Initial();

        var firstSequence = Enumerable.Range(0, 12)
            .Select(_ => first.SelectLegalMove(state))
            .ToArray();
        var secondSequence = Enumerable.Range(0, 12)
            .Select(_ => second.SelectLegalMove(state))
            .ToArray();

        Assert.Equal(firstSequence, secondSequence);
        Assert.Equal(
            [
                new Move(5, 1), new Move(5, 1), new Move(5, 0), new Move(0, 0),
                new Move(2, 7), new Move(1, 6), new Move(2, 1), new Move(2, 5),
                new Move(7, 1), new Move(4, 2), new Move(8, 8), new Move(6, 7)
            ],
            firstSequence);
        Assert.All(firstSequence, move => Assert.True(UltimateTicTacToe.IsLegalMove(state, move)));
        Assert.Equal("splitmix64", first.Name);
        Assert.Equal(1, first.Version);
    }

    [Fact]
    public void StateHashIsCanonicalAndIncludesRuleRelevantState()
    {
        var a = GameState.Initial();
        var b = GameState.Create(new Cell[81], Cell.X);
        var moved = UltimateTicTacToe.ApplyMove(a, new Move(0, 0));

        var initialHash = StateHasher.Compute(a);

        Assert.Equal(initialHash, StateHasher.Compute(b));
        Assert.NotEqual(initialHash, StateHasher.Compute(moved));
        Assert.Matches("^[0-9a-f]{64}$", initialHash);
        Assert.Equal("utt-state", StateHasher.Format);
        Assert.Equal(2, StateHasher.Version);
    }

    [Fact]
    public void VersionedConfigurationsAreImmutableValues()
    {
        Assert.Equal(new Ruleset("ultimate-tic-tac-toe", 1), Ruleset.Standard);
        Assert.Equal("untimed", TimeControl.Untimed.Name);
        Assert.Equal(1, FailurePolicy.Strict.Version);
        Assert.Equal(256, ResourceProfile.Default.MaximumMemoryMegabytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => new Ruleset("rules", 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TimeControl("clock", 1, TimeSpan.FromSeconds(-1), TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ResourceProfile("small", 1, 0, 1));
    }

    private static readonly Cell[] DrawBoard =
    [
        Cell.X, Cell.O, Cell.X,
        Cell.O, Cell.X, Cell.O,
        Cell.O, Cell.X, Cell.O
    ];

    private static Cell[] EmptyCells() => new Cell[81];

    private static Cell[] WinningBoard(Cell player) =>
    [
        player, player, player,
        Cell.Empty, Cell.Empty, Cell.Empty,
        Cell.Empty, Cell.Empty, Cell.Empty
    ];

    private static Cell Opponent(Cell player) => player == Cell.X ? Cell.O : Cell.X;

    private static void SetBoard(Cell[] cells, int board, IReadOnlyList<Cell> values)
    {
        for (var cell = 0; cell < 9; cell++)
        {
            cells[(board * 9) + cell] = values[cell];
        }
    }
}
