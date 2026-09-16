namespace Trading.Backtesting.Reporting;

public sealed record TimeBucketDefinition(string Label, TimeOnly StartInclusive, TimeOnly EndExclusive);

public sealed record BacktestReportSettings
{
    public IReadOnlyList<TimeBucketDefinition> TimeBuckets { get; init; } = Array.AsReadOnly(
    new TimeBucketDefinition[]
    {
        new("Open", new(9, 15), new(9, 45)),
        new("Morning", new(9, 45), new(11, 0)),
        new("Midday", new(11, 0), new(13, 30)),
        new("Afternoon", new(13, 30), new(15, 30))
    });
}
