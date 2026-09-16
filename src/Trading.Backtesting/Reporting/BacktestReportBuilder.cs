using Trading.Backtesting.Metrics;
using Trading.Domain.MarketData;

namespace Trading.Backtesting.Reporting;

public static class BacktestReportBuilder
{
    public static BacktestReport Build(BacktestResult result, IReadOnlyList<Candle> candles,
        TimeZoneInfo exchangeTimeZone, BacktestReportSettings? reportSettings = null,
        PerformanceMetricSettings? metricSettings = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(exchangeTimeZone);
        reportSettings ??= new();
        ValidateBuckets(reportSettings.TimeBuckets);

        var metrics = BacktestMetricsCalculator.Calculate(result, candles, exchangeTimeZone, metricSettings);
        var trades = result.Trades;
        var netR = trades.Select(NetR).ToArray();
        var durations = trades.Select(trade =>
            (decimal)(trade.ExitBarOpenTimeUtc - trade.EntryBarOpenTimeUtc).Ticks / TimeSpan.TicksPerMinute).ToArray();
        var sessions = candles.Select(candle => ExchangeDate(candle.OpenTimeUtc, exchangeTimeZone)).Distinct().ToArray();

        var monthly = trades.GroupBy(trade => ExchangeDate(trade.ExitBarOpenTimeUtc, exchangeTimeZone))
            .GroupBy(group => $"{group.Key.Year:D4}-{group.Key.Month:D2}")
            .Select(group => Breakdown(group.Key, group.SelectMany(tradesByDay => tradesByDay)))
            .OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
        var yearly = trades.GroupBy(trade => ExchangeDate(trade.ExitBarOpenTimeUtc, exchangeTimeZone).Year.ToString())
            .Select(group => Breakdown(group.Key, group)).OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
        var weekdays = trades.GroupBy(trade => ExchangeDate(trade.EntryBarOpenTimeUtc, exchangeTimeZone).DayOfWeek)
            .OrderBy(group => WeekdayOrder(group.Key))
            .Select(group => Breakdown(group.Key.ToString(), group)).ToArray();
        var timeBuckets = reportSettings.TimeBuckets.Select(bucket => Breakdown(bucket.Label,
            trades.Where(trade =>
            {
                var time = ExchangeTime(trade.EntryBarOpenTimeUtc, exchangeTimeZone);
                return time >= bucket.StartInclusive && time < bucket.EndExclusive;
            }))).ToList();
        var outside = trades.Where(trade =>
        {
            var time = ExchangeTime(trade.EntryBarOpenTimeUtc, exchangeTimeZone);
            return !reportSettings.TimeBuckets.Any(bucket => time >= bucket.StartInclusive && time < bucket.EndExclusive);
        }).ToArray();
        if (outside.Length > 0) timeBuckets.Add(Breakdown("Outside configured buckets", outside));

        return new(
            1,
            trades.Select(trade => trade.StrategyId).Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            candles.Count > 0 ? candles[0].InstrumentId : null,
            candles.Count > 0 ? candles[0].Timeframe : null,
            sessions.Length > 0 ? sessions[0] : null,
            sessions.Length > 0 ? sessions[^1] : null,
            metrics,
            new(
                netR.Length > 0 ? netR.Sum() : null,
                Median(netR),
                MaximumDrawdown(netR),
                durations.Length > 0 ? durations.Average() : null,
                Median(durations),
                Rate(trades, BacktestExitReason.Target),
                Rate(trades, BacktestExitReason.StopLoss),
                Rate(trades, BacktestExitReason.SessionExit),
                Rate(trades, BacktestExitReason.EndOfData)),
            Array.AsReadOnly(monthly),
            Array.AsReadOnly(yearly),
            Array.AsReadOnly(weekdays),
            timeBuckets.AsReadOnly());
    }

    private static PerformanceBreakdown Breakdown(string key, IEnumerable<BacktestTrade> source)
    {
        var trades = source.ToArray();
        var wins = trades.Count(trade => trade.NetPnl > 0);
        var losses = trades.Count(trade => trade.NetPnl < 0);
        return new(key, trades.Length, wins, losses, trades.Length - wins - losses,
            trades.Sum(trade => trade.NetPnl), trades.Length > 0 ? (decimal)wins / trades.Length : null,
            trades.Length > 0 ? trades.Average(NetR) : null);
    }

    private static decimal NetR(BacktestTrade trade) =>
        trade.NetPnl / (decimal.Abs(trade.EntryPrice - trade.StopPrice) * trade.Quantity);

    private static decimal? Median(IReadOnlyCollection<decimal> values)
    {
        if (values.Count == 0) return null;
        var ordered = values.Order().ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 1 ? ordered[middle] : (ordered[middle - 1] + ordered[middle]) / 2m;
    }

    private static decimal? MaximumDrawdown(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0) return null;
        var cumulative = 0m;
        var peak = 0m;
        var maximum = 0m;
        foreach (var value in values)
        {
            cumulative += value;
            peak = decimal.Max(peak, cumulative);
            maximum = decimal.Max(maximum, peak - cumulative);
        }
        return maximum;
    }

    private static decimal? Rate(IReadOnlyCollection<BacktestTrade> trades, BacktestExitReason reason) =>
        trades.Count > 0 ? (decimal)trades.Count(trade => trade.ExitReason == reason) / trades.Count : null;

    private static int WeekdayOrder(DayOfWeek value) => value == DayOfWeek.Sunday ? 6 : (int)value - 1;

    private static DateOnly ExchangeDate(DateTime utc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, zone));

    private static TimeOnly ExchangeTime(DateTime utc, TimeZoneInfo zone) =>
        TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, zone));

    private static void ValidateBuckets(IReadOnlyList<TimeBucketDefinition> buckets)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TimeOnly? previousEnd = null;
        foreach (var bucket in buckets.OrderBy(bucket => bucket.StartInclusive))
        {
            if (string.IsNullOrWhiteSpace(bucket.Label) || !labels.Add(bucket.Label) ||
                bucket.StartInclusive >= bucket.EndExclusive ||
                previousEnd is not null && bucket.StartInclusive < previousEnd)
                throw new ArgumentException("Time buckets require unique labels, positive ranges and no overlap.", nameof(buckets));
            previousEnd = bucket.EndExclusive;
        }
    }
}
