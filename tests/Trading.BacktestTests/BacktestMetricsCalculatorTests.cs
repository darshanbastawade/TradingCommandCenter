using Trading.Backtesting;
using Trading.Backtesting.Costs;
using Trading.Backtesting.Metrics;
using Trading.Domain.MarketData;
using Trading.Strategies.Contracts;

namespace Trading.BacktestTests;

public sealed class BacktestMetricsCalculatorTests
{
    private static readonly Guid InstrumentId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T09:15:00+05:30");
    private static readonly TimeZoneInfo India = TimeZoneInfo.CreateCustomTimeZone(
        "Metrics India", TimeSpan.FromMinutes(330), "Metrics India", "Metrics India");

    [Fact]
    public void Calculates_trade_drawdown_expectancy_and_risk_adjusted_metrics()
    {
        var trades = new[]
        {
            Trade(0, 100m, 1_100m),
            Trade(1, -110m, 990m),
            Trade(2, 99m, 1_089m)
        };
        var metrics = Calculate(new(1_000m, 1_089m, trades, []), Sessions(3));

        Assert.Equal(3, metrics.TotalTrades);
        Assert.Equal(2, metrics.WinningTrades);
        Assert.Equal(1, metrics.LosingTrades);
        Assert.Equal(89m, metrics.GrossTradingPnl);
        Assert.Equal(89m, metrics.NetPnl);
        Assert.Equal(199m, metrics.TotalNetProfits);
        Assert.Equal(110m, metrics.TotalNetLosses);
        Assert.Equal(0.089m, metrics.TotalReturn);
        Assert.Equal(2m / 3m, metrics.WinRate);
        Assert.Equal(99.5m, metrics.AverageWin);
        Assert.Equal(110m, metrics.AverageLoss);
        Assert.Equal(199m / 220m, metrics.PayoffRatio);
        Assert.Equal(199m / 110m, metrics.NetProfitFactor);
        Assert.Equal(89m / 3m, metrics.ExpectancyPerTrade);
        Assert.Equal(1.78m / 3m, metrics.AverageNetRMultiple);
        Assert.Equal(1, metrics.MaximumConsecutiveWins);
        Assert.Equal(1, metrics.MaximumConsecutiveLosses);
        Assert.Equal(110m, metrics.ClosedEquityMaximumDrawdown);
        Assert.Equal(0.1m, metrics.ClosedEquityMaximumDrawdownPercent);
        Assert.Equal(DateOnly.FromDateTime(Start.Date), metrics.DrawdownPeakSession);
        Assert.Equal(DateOnly.FromDateTime(Start.AddDays(1).Date), metrics.DrawdownTroughSession);
        Assert.Equal(89m / 110m, metrics.RecoveryFactor);
        Assert.Equal(4.5825756949d, metrics.AnnualizedSharpeRatio!.Value, 9);
        Assert.Equal(9.1651513899d, metrics.AnnualizedSortinoRatio!.Value, 9);
        Assert.NotNull(metrics.AnnualizedReturn);
        Assert.Equal(new decimal[] { 0.1m, -0.1m, 0.1m },
            metrics.EquityCurve.Select(point => point.PeriodReturn!.Value));
    }

    [Fact]
    public void Includes_observed_zero_trade_sessions_in_daily_return_series()
    {
        var trade = Trade(2, 100m, 1_100m);
        var metrics = Calculate(new(1_000m, 1_100m, [trade], []), Sessions(3));

        Assert.Equal(3, metrics.EquityCurve.Count);
        Assert.Equal(new decimal[] { 0, 0, 0.1m },
            metrics.EquityCurve.Select(point => point.PeriodReturn!.Value));
        Assert.Null(metrics.NetProfitFactor);
        Assert.Null(metrics.PayoffRatio);
        Assert.Null(metrics.AnnualizedSortinoRatio);
    }

    [Fact]
    public void Closed_equity_drawdown_preserves_same_session_loss_before_recovery()
    {
        var first = Trade(0, -100m, 900m);
        var second = Trade(0, 100m, 1_000m) with
        {
            EntryBarOpenTimeUtc = first.EntryBarOpenTimeUtc.AddMinutes(5),
            ExitBarOpenTimeUtc = first.ExitBarOpenTimeUtc.AddMinutes(5)
        };
        var metrics = Calculate(new(1_000m, 1_000m, [first, second], []), Sessions(1));

        Assert.Equal(0m, Assert.Single(metrics.EquityCurve).DrawdownAmount);
        Assert.Equal(100m, metrics.ClosedEquityMaximumDrawdown);
        Assert.Equal(0.1m, metrics.ClosedEquityMaximumDrawdownPercent);
    }

    [Fact]
    public void Empty_result_has_defined_zero_totals_and_nullable_undefined_ratios()
    {
        var metrics = Calculate(new(1_000m, 1_000m, [], []), Sessions(2));

        Assert.Equal(0, metrics.TotalTrades);
        Assert.Equal(0m, metrics.NetPnl);
        Assert.Equal(0m, metrics.TotalReturn);
        Assert.Null(metrics.WinRate);
        Assert.Null(metrics.ExpectancyPerTrade);
        Assert.Null(metrics.NetProfitFactor);
        Assert.Null(metrics.AnnualizedSharpeRatio);
        Assert.Null(metrics.AnnualizedSortinoRatio);
        Assert.Equal(0m, metrics.ClosedEquityMaximumDrawdown);
        Assert.Equal(0m, metrics.ClosedEquityMaximumDrawdownPercent);
        Assert.Equal(0d, metrics.AnnualizedReturn);
    }

    [Fact]
    public void Inconsistent_ledger_missing_session_and_invalid_settings_are_rejected()
    {
        var trade = Trade(0, 100m, 1_100m);
        Assert.Throws<InvalidOperationException>(() =>
            Calculate(new(1_000m, 1_099m, [trade], []), Sessions(1)));
        Assert.Throws<InvalidOperationException>(() =>
            Calculate(new(1_000m, 1_100m, [trade with { StopPrice = trade.EntryPrice }], []), Sessions(1)));
        Assert.Throws<ArgumentException>(() =>
            Calculate(new(1_000m, 1_100m, [trade], []), Sessions(1).Skip(1).ToArray()));
        Assert.Throws<ArgumentOutOfRangeException>(() => BacktestMetricsCalculator.Calculate(
            new(1_000m, 1_000m, [], []), Sessions(1), India, new() { PeriodsPerYear = 0 }));
        var otherInstrument = new[]
        {
            new Candle(Guid.NewGuid(), Timeframe.Minute5, Start, 100, 101, 99, 100, 100)
        };
        Assert.Throws<ArgumentException>(() =>
            Calculate(new(1_000m, 1_100m, [trade], []), otherInstrument));
    }

    private static BacktestMetrics Calculate(BacktestResult result, IReadOnlyList<Candle> candles) =>
        BacktestMetricsCalculator.Calculate(result, candles, India);

    private static BacktestTrade Trade(int day, decimal pnl, decimal capital)
    {
        var time = Start.AddDays(day).UtcDateTime;
        return new("metrics-test", InstrumentId, TradeDirection.Long, time, time, time, 50,
            100m, 99m, 103m, 102m, BacktestExitReason.Target, pnl, TradeCostBreakdown.None,
            0m, pnl, capital);
    }

    private static Candle[] Sessions(int count) => Enumerable.Range(0, count).Select(day =>
        new Candle(InstrumentId, Timeframe.Minute5, Start.AddDays(day), 100, 101, 99, 100, 100)).ToArray();
}
