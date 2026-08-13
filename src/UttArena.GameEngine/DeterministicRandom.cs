namespace UttArena.GameEngine;

public sealed class DeterministicPrng
{
    private ulong _state;

    public DeterministicPrng(ulong seed)
    {
        _state = seed;
    }

    public const string AlgorithmName = "splitmix64";
    public const int AlgorithmVersion = 1;

    public string Name => AlgorithmName;
    public int Version => AlgorithmVersion;

    public ulong NextUInt64()
    {
        _state += 0x9E3779B97F4A7C15UL;
        var value = _state;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    public int NextIndex(int exclusiveUpperBound)
    {
        if (exclusiveUpperBound <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(exclusiveUpperBound),
                "Upper bound must be positive.");
        }

        var bound = (ulong)exclusiveUpperBound;
        var rejectionThreshold = unchecked(0UL - bound) % bound;
        ulong value;
        do
        {
            value = NextUInt64();
        }
        while (value < rejectionThreshold);

        return (int)(value % bound);
    }

    public Move SelectLegalMove(GameState state)
    {
        var legalMoves = UltimateTicTacToe.GetLegalMoves(state);
        if (legalMoves.Count == 0)
        {
            throw new InvalidOperationException("The state has no legal moves.");
        }

        return legalMoves[NextIndex(legalMoves.Count)];
    }
}
