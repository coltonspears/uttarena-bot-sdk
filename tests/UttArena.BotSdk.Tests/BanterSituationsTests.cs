using UttArena.BotSdk.CSharp;
using UttArena.Contracts;

namespace UttArena.BotSdk.Tests;

public sealed class BanterSituationsTests
{
    [Fact]
    public void OpeningPositionFlagsOpeningAndFreeMove()
    {
        var request = Opening();
        var situations = BanterSituations.Analyze(request);

        Assert.True(situations.HasFlag(BanterSituation.Opening));
        Assert.True(situations.HasFlag(BanterSituation.FreeMove));
    }

    private static MoveRequest Opening()
    {
        var cells = Enumerable.Repeat(CellState.Empty, ProtocolConstants.CellCount).ToArray();
        var localBoards = Enumerable.Range(0, ProtocolConstants.LocalBoardCount)
            .Select(index => new LocalBoardState(index, LocalBoardStatus.Open))
            .ToArray();
        return new MoveRequest(
            ProtocolConstants.Version,
            ProtocolConstants.MoveRequestType,
            "r",
            "m",
            PlayerMark.X,
            new BoardState(cells, localBoards, null, PlayerMark.X, 0),
            [],
            [new BoardPosition(4, 4)],
            new OpponentMetadata("o", "O", null),
            RulesetDescriptor.Standard,
            new MoveTiming(1000, null, 0, DateTimeOffset.UtcNow.AddSeconds(1)));
    }
}
