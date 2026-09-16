using Trading.Backtesting;
using Trading.Backtesting.Validation;
using Trading.Domain.MarketData;
using Trading.Strategies.Contracts;

namespace Trading.BacktestTests;

public sealed class TimeSeriesValidationEngineTests
{
    private static readonly Guid InstrumentId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T09:15:00+05:30");
    private static readonly TimeZoneInfo India = TimeZoneInfo.CreateCustomTimeZone(
        "Validation India", TimeSpan.FromMinutes(330), "Validation India", "Validation India");

    [Fact]
    public void Holdout_exposes_only_training_data_to_selector_and_scores_later_sessions()
    {
        IReadOnlyList<Candle>? selectedFrom = null;
        EverySessionStrategy? selected = null;
        var result = TimeSeriesValidationEngine.RunHoldout((training, _) =>
        {
            selectedFrom = training;
            selected = new();
            return selected;
        }, Sessions(6), India, new(TrainingSessionCount: 2, EmbargoSessionCount: 1),
            new() { InitialCapital = 1_000m });

        Assert.NotNull(selectedFrom);
        Assert.Equal(4, selectedFrom.Count);
        Assert.Equal(Session(1), ExchangeDate(selectedFrom[^1]));
        Assert.Equal(6, selected!.EvaluatedSessionCount);
        Assert.Equal(Session(0), result.Window.TrainingStartSession);
        Assert.Equal(Session(1), result.Window.TrainingEndSession);
        Assert.Equal(Session(3), result.Window.TestingStartSession);
        Assert.Equal(Session(5), result.Window.TestingEndSession);
        Assert.Equal(3, result.Window.TestingSessionCount);
        Assert.Equal(3, result.OutOfSampleBacktest.Trades.Count);
        Assert.All(result.OutOfSampleBacktest.Trades,
            trade => Assert.InRange(ExchangeDate(trade.SignalBarOpenTimeUtc), Session(3), Session(5)));
        Assert.Equal(1_009m, result.OutOfSampleBacktest.FinalCapital);
        Assert.Equal(3, result.OutOfSampleMetrics.EquityCurve.Count);
    }

    [Fact]
    public void Rolling_walk_forward_uses_only_prior_training_and_non_overlapping_test_windows()
    {
        var selections = new List<(DateOnly Start, DateOnly End, int Sessions)>();
        var result = TimeSeriesValidationEngine.RunWalkForward((training, _) =>
        {
            selections.Add((ExchangeDate(training[0]), ExchangeDate(training[^1]),
                training.Select(ExchangeDate).Distinct().Count()));
            return new EverySessionStrategy();
        }, Sessions(9), India, new(TrainingSessionCount: 3, TestingSessionCount: 2, EmbargoSessionCount: 1),
            new() { InitialCapital = 1_000m });

        Assert.Equal(2, result.Folds.Count);
        Assert.Equal((Session(0), Session(2), 3), selections[0]);
        Assert.Equal((Session(2), Session(4), 3), selections[1]);
        Assert.Equal(Session(4), result.Folds[0].Window.TestingStartSession);
        Assert.Equal(Session(5), result.Folds[0].Window.TestingEndSession);
        Assert.Equal(Session(6), result.Folds[1].Window.TestingStartSession);
        Assert.Equal(Session(7), result.Folds[1].Window.TestingEndSession);
        Assert.Equal(1, result.UnusedTrailingSessionCount);
        Assert.Equal(4, result.CombinedOutOfSampleBacktest.Trades.Count);
        Assert.Equal(1_012m, result.CombinedOutOfSampleBacktest.FinalCapital);
        Assert.Equal(4, result.CombinedOutOfSampleMetrics.EquityCurve.Count);
        Assert.Equal(2, result.ProfitableFoldCount);
        Assert.Equal(1m, result.ProfitableFoldRate);
    }

    [Fact]
    public void Anchored_walk_forward_expands_training_window_without_exposing_current_test()
    {
        var selections = new List<(DateOnly Start, DateOnly End, int Sessions)>();
        var result = TimeSeriesValidationEngine.RunWalkForward((training, _) =>
        {
            selections.Add((ExchangeDate(training[0]), ExchangeDate(training[^1]),
                training.Select(ExchangeDate).Distinct().Count()));
            return new EverySessionStrategy();
        }, Sessions(8), India, new(TrainingSessionCount: 3, TestingSessionCount: 2,
            EmbargoSessionCount: 1, AnchoredTraining: true));

        Assert.Equal(2, result.Folds.Count);
        Assert.Equal((Session(0), Session(2), 3), selections[0]);
        Assert.Equal((Session(0), Session(4), 5), selections[1]);
        Assert.All(result.Folds, fold => Assert.True(fold.Window.TrainingEndSession < fold.Window.TestingStartSession));
    }

    [Fact]
    public void Combined_out_of_sample_ledger_rebases_fold_capital_chronologically()
    {
        var result = TimeSeriesValidationEngine.RunWalkForward((_, _) => new EverySessionStrategy(),
            Sessions(7), India, new(TrainingSessionCount: 2, TestingSessionCount: 2, EmbargoSessionCount: 1),
            new() { InitialCapital = 500m });

        Assert.Equal(new[] { 503m, 506m, 509m, 512m },
            result.CombinedOutOfSampleBacktest.Trades.Select(trade => trade.CapitalAfterTrade));
        Assert.Equal(12m, result.CombinedOutOfSampleMetrics.NetPnl);
    }

    [Fact]
    public void Invalid_windows_candles_and_selector_results_are_rejected()
    {
        TrainingStrategySelector selector = (_, _) => new EverySessionStrategy();
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeSeriesValidationEngine.RunHoldout(
            selector, Sessions(3), India, new(0)));
        Assert.Throws<ArgumentException>(() => TimeSeriesValidationEngine.RunHoldout(
            selector, Sessions(3), India, new(2, 1)));
        Assert.Throws<ArgumentException>(() => TimeSeriesValidationEngine.RunWalkForward(
            selector, Sessions(4), India, new(2, 2, 1)));
        Assert.Throws<InvalidOperationException>(() => TimeSeriesValidationEngine.RunHoldout(
            (_, _) => null!, Sessions(3), India, new(2)));

        var candles = Sessions(3);
        var mixed = candles.Append(new Candle(Guid.NewGuid(), Timeframe.Minute5, Start.AddDays(4),
            100, 104, 99, 103, 100)).ToArray();
        Assert.Throws<ArgumentException>(() => TimeSeriesValidationEngine.RunHoldout(
            selector, mixed, India, new(2)));
    }

    private static Candle[] Sessions(int count) => Enumerable.Range(0, count).SelectMany(day => new[]
    {
        Bar(Start.AddDays(day), 100, 101, 99, 100),
        Bar(Start.AddDays(day).AddMinutes(5), 100, 104, 100, 103)
    }).ToArray();

    private static Candle Bar(DateTimeOffset time, decimal open, decimal high, decimal low, decimal close) =>
        new(InstrumentId, Timeframe.Minute5, time, open, high, low, close, 100);

    private static DateOnly Session(int day) => DateOnly.FromDateTime(Start.AddDays(day).Date);

    private static DateOnly ExchangeDate(Candle candle) => ExchangeDate(candle.OpenTimeUtc);

    private static DateOnly ExchangeDate(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, India));

    private sealed class EverySessionStrategy : ITradingStrategy
    {
        public string Id => "walk-forward-test";
        public string Name => "Walk-forward test";
        public int EvaluatedSessionCount { get; private set; }

        public IReadOnlyList<StrategySignal> Evaluate(IReadOnlyList<Candle> candles, TimeZoneInfo exchangeTimeZone)
        {
            EvaluatedSessionCount = candles.Select(candle => ExchangeDate(candle.OpenTimeUtc)).Distinct().Count();
            return candles.GroupBy(candle => ExchangeDate(candle.OpenTimeUtc)).Select(group =>
            {
                var candle = group.First();
                return new StrategySignal(Id, candle.InstrumentId, candle.OpenTimeUtc, TradeDirection.Long,
                    candle.Close, candle.Close - 1m, candle.Close + 3m, 1m, 3m,
                    new(1, 1, 1, 1, 25, 20, 10, 100, 80, candle.Close));
            }).ToArray();
        }
    }
}
