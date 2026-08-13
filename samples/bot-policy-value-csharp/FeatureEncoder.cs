using UttArena.GameEngine;

namespace UttArena.PolicyValueBot;

internal static class FeatureEncoder
{
    public static float[] Encode(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var features = new float[PolicyValueNetwork.FeatureCount];
        var current = state.NextPlayer;
        var opponent = current == Cell.X ? Cell.O : Cell.X;
        var currentWin = current == Cell.X ? BoardResult.XWin : BoardResult.OWin;
        var opponentWin = current == Cell.X ? BoardResult.OWin : BoardResult.XWin;

        for (var board = 0; board < 9; board++)
        {
            for (var cell = 0; cell < 9; cell++)
            {
                var value = state.GetCell(board, cell);
                var global = GlobalIndex(board, cell);
                if (value == current)
                {
                    features[global] = 1;
                }
                else if (value == opponent)
                {
                    features[81 + global] = 1;
                }
            }
        }

        for (var board = 0; board < 9; board++)
        {
            var result = state.GetSubBoardResult(board);
            var channel = result == currentWin
                ? 2
                : result == opponentWin
                    ? 3
                    : result == BoardResult.Draw
                        ? 4
                        : -1;
            if (channel < 0)
            {
                continue;
            }

            FillBoard(features, channel, board);
        }

        foreach (var move in UltimateTicTacToe.GetLegalMoves(state))
        {
            features[(5 * 81) + GlobalIndex(move.Board, move.Cell)] = 1;
        }

        if (state.ForcedBoard is int forcedBoard)
        {
            FillBoard(features, 6, forcedBoard);
        }

        return features;
    }

    public static bool[] LegalMask(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var mask = new bool[PolicyValueNetwork.ActionCount];
        foreach (var move in UltimateTicTacToe.GetLegalMoves(state))
        {
            mask[(move.Board * 9) + move.Cell] = true;
        }

        return mask;
    }

    private static void FillBoard(float[] features, int channel, int board)
    {
        for (var cell = 0; cell < 9; cell++)
        {
            features[(channel * 81) + GlobalIndex(board, cell)] = 1;
        }
    }

    private static int GlobalIndex(int board, int cell)
    {
        var row = ((board / 3) * 3) + (cell / 3);
        var column = ((board % 3) * 3) + (cell % 3);
        return (row * 9) + column;
    }
}
