namespace Trading.Strategies.OpeningRangeBreakout;

public sealed record OpeningRangeBreakoutParameters
{
    public int FastEmaPeriod { get; init; } = 20;
    public int SlowEmaPeriod { get; init; } = 50;
    public int AtrPeriod { get; init; } = 14;
    public int AdxPeriod { get; init; } = 14;
    public int VolumeAveragePeriod { get; init; } = 20;
    public int OpeningRangeBars { get; init; } = 3;
    public decimal VolumeMultiplier { get; init; } = 1.2m;
    public decimal AtrStopMultiple { get; init; } = 1m;
    public decimal RewardRiskMultiple { get; init; } = 3m;
    public TimeOnly EntryWindowStart { get; init; } = new(9, 30);
    public TimeOnly EntryWindowEnd { get; init; } = new(11, 30);
}
