using Trading.Strategies.Regimes;

namespace Trading.Backtesting.Reporting;

public sealed record RegimePerformanceRow(
    string Dimension,
    string Regime,
    int TotalTrades,
    int WinningTrades,
    decimal NetPnl,
    decimal? WinRate,
    decimal? AverageNetR);

public static class RegimePerformanceAnalyzer
{
    public static IReadOnlyList<RegimePerformanceRow> Analyze(BacktestResult result,
        IReadOnlyList<MarketRegimeSnapshot> regimes)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(regimes);
        var lookup = regimes.ToDictionary(item => item.OpenTimeUtc);
        if (result.Trades.Any(trade => !lookup.ContainsKey(trade.SignalBarOpenTimeUtc)))
            throw new ArgumentException("Every trade signal must have a matching regime snapshot.", nameof(regimes));

        var classified = result.Trades.Select(trade => (Trade: trade, Regime: lookup[trade.SignalBarOpenTimeUtc])).ToArray();
        return new[]
        {
            Rows("Trend", classified, item => item.Regime.Trend.ToString()),
            Rows("Volatility", classified, item => item.Regime.Volatility.ToString()),
            Rows("Gap", classified, item => item.Regime.Gap.ToString())
        }.SelectMany(rows => rows).ToArray();
    }

    private static IEnumerable<RegimePerformanceRow> Rows(string dimension,
        IEnumerable<(BacktestTrade Trade, MarketRegimeSnapshot Regime)> source,
        Func<(BacktestTrade Trade, MarketRegimeSnapshot Regime), string> keySelector) =>
        source.GroupBy(keySelector).OrderBy(group => group.Key, StringComparer.Ordinal).Select(group =>
        {
            var trades = group.Select(item => item.Trade).ToArray();
            var winners = trades.Count(trade => trade.NetPnl > 0);
            return new RegimePerformanceRow(dimension, group.Key, trades.Length, winners,
                trades.Sum(trade => trade.NetPnl), trades.Length == 0 ? null : (decimal)winners / trades.Length,
                trades.Length == 0 ? null : trades.Average(NetR));
        });

    private static decimal NetR(BacktestTrade trade) =>
        trade.NetPnl / (decimal.Abs(trade.EntryPrice - trade.StopPrice) * trade.Quantity);
}
