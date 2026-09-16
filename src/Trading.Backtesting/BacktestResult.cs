namespace Trading.Backtesting;

public enum IgnoredCandidateReason
{
    PositionAlreadyOpen = 1,
    NoFollowingBar = 2,
    FollowingBarInDifferentSession = 3,
    FollowingBarAtOrAfterSessionExit = 4,
    InvalidEntryLevels = 5,
    InsufficientRiskOrCapital = 6
}

public sealed record IgnoredCandidate(DateTime SignalTimeUtc, IgnoredCandidateReason Reason);

public sealed record BacktestResult(
    decimal InitialCapital,
    decimal FinalCapital,
    IReadOnlyList<BacktestTrade> Trades,
    IReadOnlyList<IgnoredCandidate> IgnoredCandidates)
{
    public decimal NetPnl => FinalCapital - InitialCapital;
    public int WinningTrades => Trades.Count(trade => trade.NetPnl > 0);
    public int LosingTrades => Trades.Count(trade => trade.NetPnl < 0);
}
