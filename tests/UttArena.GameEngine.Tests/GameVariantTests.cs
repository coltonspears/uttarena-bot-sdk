using System.Globalization;

namespace UttArena.GameEngine.Tests;

public sealed class GameVariantTests
{
    [Fact]
    public void AnarchyNeverForcesABoard()
    {
        var state = GameState.Initial();

        for (var ply = 0; ply < 12; ply++)
        {
            var move = UltimateTicTacToe.GetLegalMoves(state)[0];
            state = UltimateTicTacToe.ApplyMove(state, move, GameVariant.Anarchy, seed: 99);
            Assert.Null(state.ForcedBoard);
        }

        Assert.True(UltimateTicTacToe.GetLegalMoves(state).Select(move => move.Board).Distinct().Count() > 1);
    }

    [Fact]
    public void ChaosIgnoresThePlayedCellAndStillForcesABoard()
    {
        var state = GameState.Initial();

        var after = UltimateTicTacToe.ApplyMove(state, new Move(4, 6), GameVariant.Chaos, seed: 12345);

        Assert.NotNull(after.ForcedBoard);
        Assert.All(
            UltimateTicTacToe.GetLegalMoves(after),
            move => Assert.Equal(after.ForcedBoard, move.Board));
    }

    [Fact]
    public void ChaosIsReproducibleForTheSameSeed()
    {
        var first = PlayChaos(seed: 4242, plies: 25);
        var second = PlayChaos(seed: 4242, plies: 25);

        Assert.Equal(first, second);
    }

    [Fact]
    public void ChaosDivergesForADifferentSeed()
    {
        var first = PlayChaos(seed: 1, plies: 25);
        var second = PlayChaos(seed: 2, plies: 25);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ChaosNeverSelectsAClosedBoard()
    {
        var state = GameState.Initial();
        var random = new DeterministicPrng(7);

        while (!state.IsTerminal)
        {
            var legalMoves = UltimateTicTacToe.GetLegalMoves(state);
            state = UltimateTicTacToe.ApplyMove(
                state,
                legalMoves[random.NextIndex(legalMoves.Count)],
                GameVariant.Chaos,
                seed: 555);
            if (state.ForcedBoard is int forced)
            {
                Assert.Equal(BoardResult.InProgress, state.GetSubBoardResult(forced));
            }
        }
    }

    [Fact]
    public void StandardBehaviourIsUnchangedByTheVariantOverload()
    {
        var state = GameState.Initial();

        var implicitStandard = UltimateTicTacToe.ApplyMove(state, new Move(4, 6));
        var explicitStandard = UltimateTicTacToe.ApplyMove(state, new Move(4, 6), GameVariant.Standard, seed: 999);

        Assert.Equal(implicitStandard, explicitStandard);
        Assert.Equal(6, explicitStandard.ForcedBoard);
    }

    [Fact]
    public void WildcardBypassesForcedBoardOnceAndOnlyConsumesOnSuccess()
    {
        var cells = new Cell[81];
        cells[(3 * 9) + 0] = Cell.O;
        var state = GameState.Create(
            cells,
            Cell.X,
            forcedBoard: 4,
            ply: 2,
            variant: GameVariant.Wildcard);

        Assert.Equal(9, UltimateTicTacToe.GetLegalMoves(state).Count);
        Assert.Equal(80, UltimateTicTacToe.GetLegalMoves(state, includeSpecial: true).Count);
        Assert.Throws<InvalidOperationException>(
            () => UltimateTicTacToe.ApplyMove(
                state,
                new Move(3, 0),
                GameVariant.Wildcard,
                seed: 0,
                useSpecial: true));
        Assert.True(state.XSpecialAvailable);

        var after = UltimateTicTacToe.ApplyMove(
            state,
            new Move(3, 5),
            GameVariant.Wildcard,
            seed: 0,
            useSpecial: true);

        Assert.Equal(Cell.X, after.GetCell(3, 5));
        Assert.False(after.XSpecialAvailable);
        Assert.True(after.OSpecialAvailable);
        Assert.Equal(5, after.ForcedBoard);

        var backToX = UltimateTicTacToe.ApplyMove(
            after,
            new Move(5, 0),
            GameVariant.Wildcard,
            seed: 0);
        Assert.Equal(Cell.X, backToX.NextPlayer);
        Assert.Empty(UltimateTicTacToe.GetLegalMoves(
            backToX,
            GameVariant.Wildcard,
            useSpecial: true));
    }

    [Fact]
    public void SuddenDeathEndsWhenAPlayerOwnsAnyThreeBoards()
    {
        var cells = new Cell[81];
        cells[(4 * 9) + 0] = Cell.X;
        cells[(4 * 9) + 1] = Cell.X;
        var ownership = Ownership((0, BoardResult.XWin), (1, BoardResult.XWin));
        var state = GameState.Create(
            cells,
            Cell.X,
            forcedBoard: 4,
            ply: 20,
            variant: GameVariant.SuddenDeath,
            subBoardResults: ownership);

        var after = UltimateTicTacToe.ApplyMove(
            state,
            new Move(4, 2),
            GameVariant.SuddenDeath,
            seed: 0);

        Assert.Equal(BoardResult.XWin, after.GetSubBoardResult(4));
        Assert.Equal(BoardResult.XWin, after.Result);
        Assert.True(after.IsTerminal);
    }

    [Fact]
    public void TerritoryWaitsForAllBoardsAndCountsDistinctMacroLines()
    {
        var partial = Ownership(
            (0, BoardResult.XWin),
            (1, BoardResult.XWin),
            (2, BoardResult.XWin));
        var inProgress = GameState.Create(
            new Cell[81],
            Cell.O,
            forcedBoard: null,
            ply: 30,
            variant: GameVariant.Territory,
            subBoardResults: partial);

        Assert.False(inProgress.IsTerminal);

        var completed = new[]
        {
            BoardResult.XWin, BoardResult.XWin, BoardResult.XWin,
            BoardResult.Draw, BoardResult.XWin, BoardResult.Draw,
            BoardResult.Draw, BoardResult.Draw, BoardResult.XWin
        };
        var final = GameState.Create(
            new Cell[81],
            Cell.O,
            forcedBoard: null,
            ply: 70,
            variant: GameVariant.Territory,
            subBoardResults: completed);

        Assert.Equal(7, final.XScore);
        Assert.Equal(0, final.OScore);
        Assert.Equal(BoardResult.XWin, final.Result);
    }

    [Fact]
    public void MisereAwardsTheGameToTheOpponentOfTheMacroLineOwner()
    {
        var cells = new Cell[81];
        cells[(2 * 9) + 0] = Cell.X;
        cells[(2 * 9) + 1] = Cell.X;
        var state = GameState.Create(
            cells,
            Cell.X,
            forcedBoard: 2,
            ply: 30,
            variant: GameVariant.Misere,
            subBoardResults: Ownership(
                (0, BoardResult.XWin),
                (1, BoardResult.XWin)));

        var after = UltimateTicTacToe.ApplyMove(
            state,
            new Move(2, 2),
            GameVariant.Misere,
            seed: 0);

        Assert.Equal(BoardResult.XWin, after.GetSubBoardResult(2));
        Assert.Equal(BoardResult.OWin, after.Result);
    }

    [Fact]
    public void CenterControlWaitsForClosureAndWeightsCenterTwice()
    {
        var early = Ownership(
            (0, BoardResult.XWin),
            (1, BoardResult.XWin),
            (2, BoardResult.XWin));
        Assert.False(GameState.Create(
            new Cell[81],
            Cell.O,
            forcedBoard: null,
            ply: 30,
            variant: GameVariant.CenterControl,
            subBoardResults: early).IsTerminal);

        var completed = Enumerable.Repeat(BoardResult.Draw, 9).ToArray();
        completed[0] = BoardResult.OWin;
        completed[4] = BoardResult.XWin;
        var final = GameState.Create(
            new Cell[81],
            Cell.X,
            forcedBoard: null,
            ply: 70,
            variant: GameVariant.CenterControl,
            subBoardResults: completed);

        Assert.Equal(2, final.XScore);
        Assert.Equal(1, final.OScore);
        Assert.Equal(BoardResult.XWin, final.Result);
    }

    [Fact]
    public void LockedArenaRoutesClockwisePastClosedTargets()
    {
        var state = GameState.Create(
            new Cell[81],
            Cell.X,
            forcedBoard: 0,
            ply: 10,
            variant: GameVariant.LockedArena,
            subBoardResults: Ownership(
                (3, BoardResult.Draw),
                (4, BoardResult.OWin)));

        var after = UltimateTicTacToe.ApplyMove(
            state,
            new Move(0, 3),
            GameVariant.LockedArena,
            seed: 0);

        Assert.Equal(5, after.ForcedBoard);
        Assert.All(UltimateTicTacToe.GetLegalMoves(after), move => Assert.Equal(5, move.Board));
    }

    [Fact]
    public void DoubleMoveKeepsIdentityForSecondMoveAndCannotStack()
    {
        var state = GameState.Initial(GameVariant.DoubleMove);

        var pending = UltimateTicTacToe.ApplyMove(
            state,
            new Move(0, 4),
            GameVariant.DoubleMove,
            seed: 0,
            useSpecial: true);

        Assert.Equal(Cell.X, pending.NextPlayer);
        Assert.True(pending.DoubleMovePending);
        Assert.False(pending.XSpecialAvailable);
        Assert.Equal(4, pending.ForcedBoard);
        Assert.False(UltimateTicTacToe.IsLegalMove(
            pending,
            new Move(4, 0),
            GameVariant.DoubleMove,
            useSpecial: true));

        var after = UltimateTicTacToe.ApplyMove(
            pending,
            new Move(4, 6),
            GameVariant.DoubleMove,
            seed: 0);

        Assert.Equal(Cell.X, after.GetCell(0, 4));
        Assert.Equal(Cell.X, after.GetCell(4, 6));
        Assert.Equal(Cell.O, after.NextPlayer);
        Assert.False(after.DoubleMovePending);
        Assert.Equal(6, after.ForcedBoard);
        Assert.True(after.OSpecialAvailable);
    }

    [Fact]
    public void DraftOpeningPlacesFourNeutralBlocksThenStartsNormalXTurn()
    {
        var state = GameState.Initial(GameVariant.DraftOpening);
        var draftMoves = new[]
        {
            new Move(0, 0),
            new Move(0, 1),
            new Move(0, 2),
            new Move(1, 0)
        };

        foreach (var move in draftMoves)
        {
            state = UltimateTicTacToe.ApplyMove(
                state,
                move,
                GameVariant.DraftOpening,
                seed: 0);
            Assert.Null(state.ForcedBoard);
            Assert.Equal(Cell.Blocked, state.GetCell(move.Board, move.Cell));
        }

        Assert.Equal(0, state.DraftMovesRemaining);
        Assert.Equal(Cell.X, state.NextPlayer);
        Assert.Equal(BoardResult.InProgress, state.GetSubBoardResult(0));

        var blockedBoard = new Cell[81];
        Array.Fill(blockedBoard, Cell.Blocked, 0, 9);
        var blockedResult = GameState.Create(
            blockedBoard,
            Cell.X,
            forcedBoard: null,
            ply: 4,
            variant: GameVariant.DraftOpening,
            draftMovesRemaining: 0);
        Assert.Equal(BoardResult.Draw, blockedResult.GetSubBoardResult(0));

        var normal = UltimateTicTacToe.ApplyMove(
            state,
            new Move(0, 3),
            GameVariant.DraftOpening,
            seed: 0);
        Assert.Equal(Cell.X, normal.GetCell(0, 3));
        Assert.Equal(3, normal.ForcedBoard);
    }

    [Fact]
    public void RelaySwapsPlacedSymbolsWithoutTransferringCapturedOwnership()
    {
        var cells = new Cell[81];
        cells[0] = Cell.X;
        cells[1] = Cell.X;
        var state = GameState.Create(
            cells,
            Cell.X,
            forcedBoard: 0,
            ply: 12,
            variant: GameVariant.Relay);

        var afterCapture = UltimateTicTacToe.ApplyMove(
            state,
            new Move(0, 2),
            GameVariant.Relay,
            seed: 0);

        Assert.Equal(BoardResult.XWin, afterCapture.GetSubBoardResult(0));
        Assert.Equal(Cell.O, afterCapture.XPlayerSymbol);
        Assert.Equal(Cell.X, afterCapture.OPlayerSymbol);
        Assert.Equal(Cell.O, afterCapture.NextPlayer);
        Assert.Equal(Cell.X, afterCapture.CurrentSymbol);

        var afterOpponentMove = UltimateTicTacToe.ApplyMove(
            afterCapture,
            new Move(2, 0),
            GameVariant.Relay,
            seed: 0);
        Assert.Equal(Cell.X, afterOpponentMove.GetCell(2, 0));
        Assert.Equal(BoardResult.XWin, afterOpponentMove.GetSubBoardResult(0));
    }

    [Fact]
    public void RelayCreditsANewGlyphLineToTheStablePlayerIdentity()
    {
        var cells = new Cell[81];
        cells[(1 * 9) + 0] = Cell.X;
        cells[(1 * 9) + 1] = Cell.X;
        var state = GameState.Create(
            cells,
            Cell.O,
            forcedBoard: 1,
            ply: 20,
            variant: GameVariant.Relay,
            subBoardResults: Ownership((0, BoardResult.XWin)),
            xPlayerSymbol: Cell.O,
            oPlayerSymbol: Cell.X);

        var after = UltimateTicTacToe.ApplyMove(
            state,
            new Move(1, 2),
            GameVariant.Relay,
            seed: 0);

        Assert.Equal(Cell.X, after.GetCell(1, 2));
        Assert.Equal(BoardResult.OWin, after.GetSubBoardResult(1));
        Assert.Equal(Cell.X, after.XPlayerSymbol);
        Assert.Equal(Cell.O, after.OPlayerSymbol);
    }

    [Fact]
    public void LastStandAllowsOneCounterLineResponse()
    {
        var state = LastStandSetup();
        var pending = UltimateTicTacToe.ApplyMove(
            state,
            new Move(2, 2),
            GameVariant.LastStand,
            seed: 0);

        Assert.False(pending.IsTerminal);
        Assert.Equal(Cell.X, pending.LastStandPendingOwner);
        Assert.Equal(Cell.O, pending.NextPlayer);

        var counter = UltimateTicTacToe.ApplyMove(
            pending,
            new Move(5, 2),
            GameVariant.LastStand,
            seed: 0);

        Assert.Equal(BoardResult.OWin, counter.GetSubBoardResult(5));
        Assert.Equal(BoardResult.OWin, counter.Result);
        Assert.Null(counter.LastStandPendingOwner);
    }

    [Fact]
    public void LastStandAwardsOriginalOwnerWhenResponseDoesNotCounter()
    {
        var pending = UltimateTicTacToe.ApplyMove(
            LastStandSetup(),
            new Move(2, 2),
            GameVariant.LastStand,
            seed: 0);

        var afterResponse = UltimateTicTacToe.ApplyMove(
            pending,
            new Move(6, 0),
            GameVariant.LastStand,
            seed: 0);

        Assert.Equal(BoardResult.XWin, afterResponse.Result);
        Assert.Equal(BoardResult.XWin, afterResponse.ResultOverride);
    }

    [Fact]
    public void StateHashIncludesAllVariantMetadataAndStableOwnership()
    {
        var cells = new Cell[81];
        var ownership = Ownership((0, BoardResult.XWin));
        var relay = GameState.Create(
            cells,
            Cell.O,
            forcedBoard: 2,
            ply: 7,
            variant: GameVariant.Relay,
            subBoardResults: ownership,
            xSpecialAvailable: false,
            oSpecialAvailable: true,
            xPlayerSymbol: Cell.O,
            oPlayerSymbol: Cell.X);
        var reconstructed = GameState.Create(
            cells,
            Cell.O,
            forcedBoard: 2,
            ply: 7,
            variant: GameVariant.Relay,
            subBoardResults: ownership,
            xSpecialAvailable: false,
            oSpecialAvailable: true,
            xPlayerSymbol: Cell.O,
            oPlayerSymbol: Cell.X);
        var differentOwnership = GameState.Create(
            cells,
            Cell.O,
            forcedBoard: 2,
            ply: 7,
            variant: GameVariant.Relay,
            subBoardResults: Ownership(),
            xSpecialAvailable: false,
            oSpecialAvailable: true,
            xPlayerSymbol: Cell.O,
            oPlayerSymbol: Cell.X);
        var differentSymbols = GameState.Create(
            cells,
            Cell.O,
            forcedBoard: 2,
            ply: 7,
            variant: GameVariant.Relay,
            subBoardResults: ownership,
            xSpecialAvailable: false,
            oSpecialAvailable: true);
        var differentSpecial = GameState.Create(
            cells,
            Cell.O,
            forcedBoard: 2,
            ply: 7,
            variant: GameVariant.Relay,
            subBoardResults: ownership,
            xSpecialAvailable: true,
            oSpecialAvailable: true,
            xPlayerSymbol: Cell.O,
            oPlayerSymbol: Cell.X);

        Assert.Equal(relay, reconstructed);
        Assert.Equal(StateHasher.Compute(relay), StateHasher.Compute(reconstructed));
        Assert.NotEqual(StateHasher.Compute(relay), StateHasher.Compute(differentOwnership));
        Assert.NotEqual(StateHasher.Compute(relay), StateHasher.Compute(differentSymbols));
        Assert.NotEqual(StateHasher.Compute(relay), StateHasher.Compute(differentSpecial));

        var ordinaryDouble = GameState.Create(
            cells,
            Cell.X,
            forcedBoard: null,
            ply: 4,
            variant: GameVariant.DoubleMove);
        var pendingDouble = GameState.Create(
            cells,
            Cell.X,
            forcedBoard: null,
            ply: 4,
            variant: GameVariant.DoubleMove,
            doubleMovePending: true);
        Assert.NotEqual(StateHasher.Compute(ordinaryDouble), StateHasher.Compute(pendingDouble));

        var draft = GameState.Create(
            cells,
            Cell.X,
            forcedBoard: null,
            ply: 2,
            variant: GameVariant.DraftOpening,
            draftMovesRemaining: 2);
        var laterDraft = GameState.Create(
            cells,
            Cell.X,
            forcedBoard: null,
            ply: 2,
            variant: GameVariant.DraftOpening,
            draftMovesRemaining: 1);
        Assert.NotEqual(StateHasher.Compute(draft), StateHasher.Compute(laterDraft));

        var lastStandOwnership = Ownership(
            (0, BoardResult.XWin),
            (1, BoardResult.XWin),
            (2, BoardResult.XWin));
        var pendingLastStand = GameState.Create(
            cells,
            Cell.O,
            forcedBoard: null,
            ply: 30,
            variant: GameVariant.LastStand,
            subBoardResults: lastStandOwnership,
            lastStandPendingOwner: Cell.X);
        var resolvedLastStand = GameState.Create(
            cells,
            Cell.O,
            forcedBoard: null,
            ply: 30,
            variant: GameVariant.LastStand,
            subBoardResults: lastStandOwnership,
            resultOverride: BoardResult.XWin);
        Assert.NotEqual(
            StateHasher.Compute(pendingLastStand),
            StateHasher.Compute(resolvedLastStand));
    }

    /// <summary>
    /// Chaos must depend only on the seed and the ply, so replaying the same move
    /// sequence has to reproduce the same forced boards and the same final hash.
    /// </summary>
    private static string PlayChaos(ulong seed, int plies)
    {
        var state = GameState.Initial();
        var chooser = new DeterministicPrng(31);
        var boards = new List<string>();

        for (var ply = 0; ply < plies && !state.IsTerminal; ply++)
        {
            var legalMoves = UltimateTicTacToe.GetLegalMoves(state);
            state = UltimateTicTacToe.ApplyMove(
                state,
                legalMoves[chooser.NextIndex(legalMoves.Count)],
                GameVariant.Chaos,
                seed);
            boards.Add(state.ForcedBoard?.ToString(CultureInfo.InvariantCulture) ?? "-");
        }

        return string.Join(',', boards) + "|" + StateHasher.Compute(state);
    }

    private static GameState LastStandSetup()
    {
        var cells = new Cell[81];
        cells[(2 * 9) + 0] = Cell.X;
        cells[(2 * 9) + 1] = Cell.X;
        cells[(5 * 9) + 0] = Cell.O;
        cells[(5 * 9) + 1] = Cell.O;
        return GameState.Create(
            cells,
            Cell.X,
            forcedBoard: 2,
            ply: 40,
            variant: GameVariant.LastStand,
            subBoardResults: Ownership(
                (0, BoardResult.XWin),
                (1, BoardResult.XWin),
                (3, BoardResult.OWin),
                (4, BoardResult.OWin)));
    }

    private static BoardResult[] Ownership(params (int Board, BoardResult Result)[] owned)
    {
        var results = Enumerable.Repeat(BoardResult.InProgress, 9).ToArray();
        foreach (var (board, result) in owned)
        {
            results[board] = result;
        }

        return results;
    }
}
