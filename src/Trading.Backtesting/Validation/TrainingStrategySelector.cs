using Trading.Domain.MarketData;
using Trading.Strategies.Contracts;

namespace Trading.Backtesting.Validation;

/// <summary>Selects or configures a strategy using training candles only.</summary>
public delegate ITradingStrategy TrainingStrategySelector(
    IReadOnlyList<Candle> trainingCandles,
    TimeZoneInfo exchangeTimeZone);
