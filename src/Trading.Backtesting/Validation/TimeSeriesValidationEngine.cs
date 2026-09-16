using Trading.Backtesting.Metrics;
using Trading.Domain.MarketData;
using Trading.Strategies.Contracts;

namespace Trading.Backtesting.Validation;

/// <summary>Chronological research validation. It never places or routes orders.</summary>
public static class TimeSeriesValidationEngine
{
    public static ValidationFold RunHoldout(
        TrainingStrategySelector selector,
        IReadOnlyList<Candle> candles,
        TimeZoneInfo exchangeTimeZone,
        HoldoutValidationSettings settings,
        BacktestSettings? backtestSettings = null,
        PerformanceMetricSettings? metricSettings = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var sessions = ValidateInputs(selector, candles, exchangeTimeZone);
        if (settings.TrainingSessionCount < 1 || settings.EmbargoSessionCount < 0)
            throw new ArgumentOutOfRangeException(nameof(settings), "Training must be positive and embargo cannot be negative.");
        var testingStart = settings.TrainingSessionCount + settings.EmbargoSessionCount;
        if (testingStart >= sessions.Count)
            throw new ArgumentException("The candle series does not contain a session after training and embargo.", nameof(candles));

        var window = Window(0, sessions, 0, settings.TrainingSessionCount,
            testingStart, sessions.Count, settings.EmbargoSessionCount);
        return RunFold(selector, candles, exchangeTimeZone, window, backtestSettings ?? new(), metricSettings);
    }

    public static WalkForwardValidationResult RunWalkForward(
        TrainingStrategySelector selector,
        IReadOnlyList<Candle> candles,
        TimeZoneInfo exchangeTimeZone,
        WalkForwardValidationSettings settings,
        BacktestSettings? backtestSettings = null,
        PerformanceMetricSettings? metricSettings = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var sessions = ValidateInputs(selector, candles, exchangeTimeZone);
        if (settings.TrainingSessionCount < 1 || settings.TestingSessionCount < 1 || settings.EmbargoSessionCount < 0)
            throw new ArgumentOutOfRangeException(nameof(settings),
                "Training and testing must be positive and embargo cannot be negative.");
        var firstTestingStart = settings.TrainingSessionCount + settings.EmbargoSessionCount;
        if (firstTestingStart + settings.TestingSessionCount > sessions.Count)
            throw new ArgumentException("The candle series does not contain one complete walk-forward fold.", nameof(candles));

        var effectiveBacktestSettings = backtestSettings ?? new();
        var folds = new List<ValidationFold>();
        var testingStart = firstTestingStart;
        while (testingStart + settings.TestingSessionCount <= sessions.Count)
        {
            var trainingEnd = testingStart - settings.EmbargoSessionCount;
            var trainingStart = settings.AnchoredTraining ? 0 : trainingEnd - settings.TrainingSessionCount;
            var window = Window(folds.Count, sessions, trainingStart, trainingEnd,
                testingStart, testingStart + settings.TestingSessionCount, settings.EmbargoSessionCount);
            folds.Add(RunFold(selector, candles, exchangeTimeZone, window, effectiveBacktestSettings, metricSettings));
            testingStart += settings.TestingSessionCount;
        }

        var outOfSampleCandles = folds.SelectMany(fold => CandlesInRange(candles, exchangeTimeZone,
            fold.Window.TestingStartSession, fold.Window.TestingEndSession)).ToArray();
        var combinedBacktest = Stitch(folds, effectiveBacktestSettings.InitialCapital);
        var combinedMetrics = BacktestMetricsCalculator.Calculate(
            combinedBacktest, outOfSampleCandles, exchangeTimeZone, metricSettings);
        return new(folds.AsReadOnly(), combinedBacktest, combinedMetrics, sessions.Count - testingStart);
    }

    private static ValidationFold RunFold(
        TrainingStrategySelector selector,
        IReadOnlyList<Candle> candles,
        TimeZoneInfo exchangeTimeZone,
        ValidationWindow window,
        BacktestSettings backtestSettings,
        PerformanceMetricSettings? metricSettings)
    {
        var training = CandlesInRange(candles, exchangeTimeZone,
            window.TrainingStartSession, window.TrainingEndSession).ToArray();
        var testing = CandlesInRange(candles, exchangeTimeZone,
            window.TestingStartSession, window.TestingEndSession).ToArray();
        var context = CandlesInRange(candles, exchangeTimeZone,
            window.TrainingStartSession, window.TestingEndSession).ToArray();
        var strategy = selector(Array.AsReadOnly(training), exchangeTimeZone) ??
            throw new InvalidOperationException("The training selector returned no strategy.");
        if (string.IsNullOrWhiteSpace(strategy.Id))
            throw new InvalidOperationException("The selected strategy must have a stable ID.");

        var gated = new TestingWindowStrategy(strategy, window.TestingStartSession,
            window.TestingEndSession, exchangeTimeZone);
        var backtest = BacktestEngine.Run(gated, context, exchangeTimeZone, backtestSettings);
        var metrics = BacktestMetricsCalculator.Calculate(backtest, testing, exchangeTimeZone, metricSettings);
        return new(window, strategy.Id, backtest, metrics);
    }

    private static BacktestResult Stitch(IReadOnlyList<ValidationFold> folds, decimal initialCapital)
    {
        var capital = initialCapital;
        var trades = folds.SelectMany(fold => fold.OutOfSampleBacktest.Trades)
            .OrderBy(trade => trade.ExitBarOpenTimeUtc)
            .Select(trade =>
            {
                capital += trade.NetPnl;
                return trade with { CapitalAfterTrade = capital };
            }).ToArray();
        var ignored = folds.SelectMany(fold => fold.OutOfSampleBacktest.IgnoredCandidates)
            .OrderBy(candidate => candidate.SignalTimeUtc).ToArray();
        return new(initialCapital, capital, Array.AsReadOnly(trades), Array.AsReadOnly(ignored));
    }

    private static ValidationWindow Window(int index, IReadOnlyList<DateOnly> sessions,
        int trainingStart, int trainingEnd, int testingStart, int testingEnd, int embargoCount) => new(
            index,
            sessions[trainingStart],
            sessions[trainingEnd - 1],
            sessions[testingStart],
            sessions[testingEnd - 1],
            trainingEnd - trainingStart,
            embargoCount,
            testingEnd - testingStart);

    private static IEnumerable<Candle> CandlesInRange(IReadOnlyList<Candle> candles, TimeZoneInfo zone,
        DateOnly start, DateOnly end) => candles.Where(candle =>
    {
        var session = ExchangeDate(candle.OpenTimeUtc, zone);
        return session >= start && session <= end;
    });

    private static List<DateOnly> ValidateInputs(TrainingStrategySelector selector,
        IReadOnlyList<Candle> candles, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(zone);
        for (var index = 1; index < candles.Count; index++)
        {
            if (candles[index].OpenTimeUtc <= candles[index - 1].OpenTimeUtc)
                throw new ArgumentException("Candles must be strictly chronological.", nameof(candles));
            if (candles[index].InstrumentId != candles[0].InstrumentId ||
                candles[index].Timeframe != candles[0].Timeframe)
                throw new ArgumentException("Candles must share one instrument and timeframe.", nameof(candles));
        }
        return candles.Select(candle => ExchangeDate(candle.OpenTimeUtc, zone)).Distinct().ToList();
    }

    private static DateOnly ExchangeDate(DateTime utc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, zone));

    private sealed class TestingWindowStrategy(
        ITradingStrategy inner,
        DateOnly testingStart,
        DateOnly testingEnd,
        TimeZoneInfo zone) : ITradingStrategy
    {
        public string Id => inner.Id;
        public string Name => inner.Name;

        public IReadOnlyList<StrategySignal> Evaluate(IReadOnlyList<Candle> candles, TimeZoneInfo exchangeTimeZone) =>
            inner.Evaluate(candles, exchangeTimeZone)
                .Where(signal =>
                {
                    var session = ExchangeDate(signal.OpenTimeUtc, zone);
                    return session >= testingStart && session <= testingEnd;
                }).ToArray();
    }
}
