using Trading.Domain.MarketData;

namespace Trading.Strategies.Contracts;

public interface ITradingStrategy
{
    string Id { get; }
    string Name { get; }

    /// <summary>Evaluates completed candles and returns candidate entries in chronological order.</summary>
    IReadOnlyList<StrategySignal> Evaluate(IReadOnlyList<Candle> candles, TimeZoneInfo exchangeTimeZone);
}
