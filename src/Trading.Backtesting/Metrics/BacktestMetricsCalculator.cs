using Trading.Domain.MarketData;

namespace Trading.Backtesting.Metrics;

public static class BacktestMetricsCalculator
{
    public static BacktestMetrics Calculate(BacktestResult result, IReadOnlyList<Candle> candles,
        TimeZoneInfo exchangeTimeZone, PerformanceMetricSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(exchangeTimeZone);
        settings ??= new();
        if (settings.PeriodsPerYear < 1) throw new ArgumentOutOfRangeException(nameof(settings));
        if (settings.PeriodicRiskFreeRate <= -1 || settings.PeriodicMinimumAcceptableReturn <= -1)
            throw new ArgumentOutOfRangeException(nameof(settings), "Periodic reference returns must be greater than -100%.");

        ValidateLedger(result);
        var sessions = Sessions(candles, exchangeTimeZone);
        if (candles.Count > 0 && result.Trades.Any(trade => trade.InstrumentId != candles[0].InstrumentId))
            throw new ArgumentException("Trades and candles must belong to the same instrument.", nameof(candles));
        var pnlBySession = result.Trades.GroupBy(trade => ExchangeDate(trade.ExitBarOpenTimeUtc, exchangeTimeZone))
            .ToDictionary(group => group.Key, group => group.Sum(trade => trade.NetPnl));
        if (pnlBySession.Keys.Any(session => !sessions.Contains(session)))
            throw new ArgumentException("Every trade exit must map to an observed candle session.", nameof(candles));

        var curve = BuildCurve(result.InitialCapital, sessions, pnlBySession);
        var drawdown = ClosedEquityDrawdown(result, exchangeTimeZone);
        var winners = result.Trades.Where(trade => trade.NetPnl > 0).ToArray();
        var losers = result.Trades.Where(trade => trade.NetPnl < 0).ToArray();
        var breakEven = result.Trades.Count - winners.Length - losers.Length;
        var grossProfit = winners.Sum(trade => trade.NetPnl);
        var grossLoss = decimal.Abs(losers.Sum(trade => trade.NetPnl));
        var (maximumWins, maximumLosses) = ConsecutiveRuns(result.Trades);
        var returns = curve.Select(point => point.PeriodReturn).ToArray();
        var completeReturns = returns.All(value => value is not null)
            ? returns.Select(value => value!.Value).ToArray()
            : [];

        return new(
            result.Trades.Count,
            winners.Length,
            losers.Length,
            breakEven,
            result.Trades.Sum(trade => trade.GrossPnl),
            result.Trades.Sum(trade => trade.Costs),
            result.NetPnl,
            grossProfit,
            grossLoss,
            result.InitialCapital > 0 ? result.NetPnl / result.InitialCapital : null,
            result.Trades.Count > 0 ? (decimal)winners.Length / result.Trades.Count : null,
            winners.Length > 0 ? grossProfit / winners.Length : null,
            losers.Length > 0 ? grossLoss / losers.Length : null,
            winners.Length > 0 && losers.Length > 0 ? (grossProfit / winners.Length) / (grossLoss / losers.Length) : null,
            grossLoss > 0 ? grossProfit / grossLoss : null,
            result.Trades.Count > 0 ? result.NetPnl / result.Trades.Count : null,
            result.Trades.Count > 0 ? result.Trades.Average(NetRMultiple) : null,
            maximumWins,
            maximumLosses,
            drawdown.Amount,
            drawdown.Percent,
            drawdown.PeakSession,
            drawdown.TroughSession,
            drawdown.Amount > 0 ? result.NetPnl / drawdown.Amount : null,
            AnnualizedReturn(result.InitialCapital, result.FinalCapital, sessions.Count, settings.PeriodsPerYear),
            Sharpe(completeReturns, settings.PeriodicRiskFreeRate, settings.PeriodsPerYear),
            Sortino(completeReturns, settings.PeriodicMinimumAcceptableReturn, settings.PeriodsPerYear),
            curve.AsReadOnly());
    }

    private static List<EquityPoint> BuildCurve(decimal initialCapital, IReadOnlyList<DateOnly> sessions,
        IReadOnlyDictionary<DateOnly, decimal> pnlBySession)
    {
        var curve = new List<EquityPoint>(sessions.Count);
        var capital = initialCapital;
        var peak = initialCapital;
        foreach (var session in sessions)
        {
            var startingCapital = capital;
            var pnl = pnlBySession.GetValueOrDefault(session);
            capital += pnl;
            decimal? periodReturn = startingCapital > 0 ? pnl / startingCapital : null;
            if (capital > peak)
                peak = capital;
            var drawdown = decimal.Max(0, peak - capital);
            decimal? drawdownPercent = peak > 0 ? drawdown / peak : null;
            curve.Add(new(session, pnl, capital, periodReturn, drawdown, drawdownPercent));
        }
        return curve;
    }

    private static DrawdownResult ClosedEquityDrawdown(BacktestResult result, TimeZoneInfo zone)
    {
        var peak = result.InitialCapital;
        DateOnly? currentPeakSession = null;
        var maximum = 0m;
        decimal? maximumPercent = peak > 0 ? 0m : null;
        DateOnly? maximumPeakSession = null;
        DateOnly? maximumTroughSession = null;
        foreach (var trade in result.Trades)
        {
            var session = ExchangeDate(trade.ExitBarOpenTimeUtc, zone);
            if (trade.CapitalAfterTrade > peak)
            {
                peak = trade.CapitalAfterTrade;
                currentPeakSession = session;
            }
            var amount = decimal.Max(0, peak - trade.CapitalAfterTrade);
            var percent = peak > 0 ? amount / peak : (decimal?)null;
            if (amount > maximum)
            {
                maximum = amount;
                maximumPercent = percent;
                maximumPeakSession = currentPeakSession;
                maximumTroughSession = session;
            }
        }
        return new(maximum, maximumPercent, maximumPeakSession, maximumTroughSession);
    }

    private static double? AnnualizedReturn(decimal initial, decimal final, int periods, int periodsPerYear)
    {
        if (initial <= 0 || final <= 0 || periods < 1) return null;
        var annualized = Math.Pow((double)(final / initial), (double)periodsPerYear / periods) - 1d;
        return double.IsFinite(annualized) ? annualized : null;
    }

    private static double? Sharpe(IReadOnlyList<decimal> returns, decimal riskFreeRate, int periodsPerYear)
    {
        if (returns.Count < 2) return null;
        var values = returns.Select(value => (double)value).ToArray();
        var mean = values.Average();
        var variance = values.Sum(value => Math.Pow(value - mean, 2)) / (values.Length - 1);
        if (variance <= 0) return null;
        return (mean - (double)riskFreeRate) / Math.Sqrt(variance) * Math.Sqrt(periodsPerYear);
    }

    private static double? Sortino(IReadOnlyList<decimal> returns, decimal minimumReturn, int periodsPerYear)
    {
        if (returns.Count < 1) return null;
        var excess = returns.Select(value => (double)(value - minimumReturn)).ToArray();
        var downsideDeviation = Math.Sqrt(excess.Sum(value => Math.Pow(Math.Min(value, 0d), 2)) / excess.Length);
        if (downsideDeviation <= 0) return null;
        return excess.Average() / downsideDeviation * Math.Sqrt(periodsPerYear);
    }

    private static decimal NetRMultiple(BacktestTrade trade) =>
        trade.NetPnl / (decimal.Abs(trade.EntryPrice - trade.StopPrice) * trade.Quantity);

    private static (int Wins, int Losses) ConsecutiveRuns(IReadOnlyList<BacktestTrade> trades)
    {
        var wins = 0;
        var losses = 0;
        var maximumWins = 0;
        var maximumLosses = 0;
        foreach (var trade in trades)
        {
            if (trade.NetPnl > 0)
            {
                wins++;
                losses = 0;
                maximumWins = int.Max(maximumWins, wins);
            }
            else if (trade.NetPnl < 0)
            {
                losses++;
                wins = 0;
                maximumLosses = int.Max(maximumLosses, losses);
            }
            else
            {
                wins = 0;
                losses = 0;
            }
        }
        return (maximumWins, maximumLosses);
    }

    private static List<DateOnly> Sessions(IReadOnlyList<Candle> candles, TimeZoneInfo zone)
    {
        for (var index = 1; index < candles.Count; index++)
        {
            if (candles[index].OpenTimeUtc <= candles[index - 1].OpenTimeUtc)
                throw new ArgumentException("Candles must be strictly chronological.", nameof(candles));
            if (candles[index].InstrumentId != candles[0].InstrumentId || candles[index].Timeframe != candles[0].Timeframe)
                throw new ArgumentException("Candles must share one instrument and timeframe.", nameof(candles));
        }
        return candles.Select(candle => ExchangeDate(candle.OpenTimeUtc, zone)).Distinct().ToList();
    }

    private static DateOnly ExchangeDate(DateTime utc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, zone));

    private static void ValidateLedger(BacktestResult result)
    {
        var capital = result.InitialCapital;
        DateTime? previousExit = null;
        foreach (var trade in result.Trades)
        {
            if (trade.Quantity < 1 || trade.EntryPrice <= 0 || trade.StopPrice <= 0 ||
                trade.EntryPrice == trade.StopPrice || trade.ExitPrice <= 0 ||
                trade.Costs < 0 || trade.Costs != trade.CostBreakdown.Total || trade.NetPnl != trade.GrossPnl - trade.Costs ||
                trade.EntryBarOpenTimeUtc > trade.ExitBarOpenTimeUtc ||
                previousExit is not null && trade.ExitBarOpenTimeUtc < previousExit)
                throw new InvalidOperationException("Backtest trade ledger is inconsistent.");
            capital += trade.NetPnl;
            if (capital != trade.CapitalAfterTrade)
                throw new InvalidOperationException("Backtest capital path is inconsistent.");
            previousExit = trade.ExitBarOpenTimeUtc;
        }
        if (capital != result.FinalCapital)
            throw new InvalidOperationException("Backtest final capital is inconsistent with its trades.");
    }

    private sealed record DrawdownResult(decimal Amount, decimal? Percent, DateOnly? PeakSession,
        DateOnly? TroughSession);
}
