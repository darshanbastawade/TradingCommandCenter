namespace Trading.Backtesting.Metrics;

public sealed record EquityPoint(
    DateOnly Session,
    decimal RealizedPnl,
    decimal ClosingCapital,
    decimal? PeriodReturn,
    decimal DrawdownAmount,
    decimal? DrawdownPercent);
