using Trading.Backtesting;
using Trading.Backtesting.Costs;
using Trading.Backtesting.Options;
using Trading.Domain.MarketData;
using Trading.Strategies.Contracts;

namespace Trading.BacktestTests;

public sealed class OptionsBacktestEngineTests
{
    private static readonly Guid Underlying = Guid.Parse("17171717-1717-1717-1717-171717171717");
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-15T09:30:00+05:30");
    private static readonly TimeZoneInfo India = TimeZoneInfo.CreateCustomTimeZone("M17 India", TimeSpan.FromMinutes(330), "M17", "M17");

    [Fact]
    public void Long_signal_buys_nearest_expiry_one_strike_itm_call_at_ask_and_sells_at_bid()
    {
        var candles = Candles();
        var near100 = Contract(100, OptionRight.Call, 1);
        var result = Run(candles, [Contract(90, OptionRight.Call, 2), near100,
            Contract(100, OptionRight.Call, 3, expiryDays: 7)],
            [Quote(near100, 5, 9.5m, 10m), Quote(near100, 10, 16.2m, 16.5m)],
            Signal(candles[0], TradeDirection.Long));

        var trade = Assert.Single(result.Trades);
        Assert.Equal(near100.Id, trade.OptionContractId);
        Assert.Equal(OptionRight.Call, trade.OptionRight);
        Assert.Equal(10m, trade.EntryPrice);
        Assert.Equal(8m, trade.StopPrice);
        Assert.Equal(16m, trade.TargetPrice);
        Assert.Equal(16.2m, trade.ExitPrice);
        Assert.Equal(50, trade.Quantity);
        Assert.Equal(BacktestExitReason.Target, trade.ExitReason);
        Assert.Equal(310m, trade.NetPnl);
        Assert.Equal(100m, trade.InitialRisk);
        Assert.Equal(3.1m, trade.NetRMultiple);
        Assert.Equal(3.1m, result.AverageNetR);
    }

    [Fact]
    public void Short_signal_buys_the_closest_itm_put()
    {
        var candles = Candles();
        var put110 = Contract(110, OptionRight.Put, 4);
        var trade = Assert.Single(Run(candles, [put110, Contract(120, OptionRight.Put, 5)],
            [Quote(put110, 5, 9.5m, 10m), Quote(put110, 10, 7.5m, 8m)],
            Signal(candles[0], TradeDirection.Short)).Trades);

        Assert.Equal(110m, trade.Strike);
        Assert.Equal(OptionRight.Put, trade.OptionRight);
        Assert.Equal(BacktestExitReason.StopLoss, trade.ExitReason);
        Assert.Equal(7.5m, trade.ExitBid);
    }

    [Theory]
    [InlineData(0, 1000, 2000, OptionCandidateRejection.InsufficientVolume)]
    [InlineData(1000, 0, 2000, OptionCandidateRejection.InsufficientOpenInterest)]
    [InlineData(1000, 1000, 10, OptionCandidateRejection.SpreadTooWide)]
    public void Liquidity_and_spread_gates_reject_untradable_contracts(long volume, long openInterest,
        decimal maximumSpread, OptionCandidateRejection expected)
    {
        var candles = Candles();
        var contract = Contract(100, OptionRight.Call, 6);
        var settings = Settings() with { MinimumVolume = 100, MinimumOpenInterest = 100, MaximumSpreadBasisPoints = maximumSpread };
        var quote = new OptionQuote(contract.Id, Start.AddMinutes(5), 9m, 10m, 9.5m, volume, openInterest);
        var result = OptionsBacktestEngine.Run(new FixedStrategy([Signal(candles[0], TradeDirection.Long)]),
            candles, [contract], [quote, Quote(contract, 10, 11m, 11.5m)], India, settings);

        Assert.Empty(result.Trades);
        Assert.Equal(expected, Assert.Single(result.RejectedCandidates).Reason);
    }

    [Fact]
    public void Slippage_rounds_adversely_to_tick_and_option_costs_reduce_pnl()
    {
        var candles = Candles();
        var contract = Contract(100, OptionRight.Call, 7);
        var settings = Settings() with { AllowedRiskPerTrade = 110, SlippageBasisPointsPerSide = 10,
            CostModel = IndianCostProfiles.ZerodhaNseEquityOptions2026() };
        var trade = Assert.Single(OptionsBacktestEngine.Run(new FixedStrategy([Signal(candles[0], TradeDirection.Long)]),
            candles, [contract], [Quote(contract, 5, 9.5m, 10m), Quote(contract, 10, 16.2m, 16.5m)],
            India, settings).Trades);

        Assert.Equal(10.05m, trade.EntryPrice);
        Assert.Equal(16.15m, trade.ExitPrice);
        Assert.True(trade.Costs > 0);
        Assert.True(trade.NetPnl < trade.GrossPnl);
    }

    [Fact]
    public void Missing_same_session_exit_quote_never_carries_an_option_overnight()
    {
        var candles = new[] { Bar(0, 104), Bar(5, 105), Bar(0, 106, day: 1) };
        var contract = Contract(100, OptionRight.Call, 8);
        var result = Run(candles, [contract], [Quote(contract, 5, 9.5m, 10m),
            new OptionQuote(contract.Id, Start.AddDays(1), 12, 12.5m, 12.25m, 1000, 1000)],
            Signal(candles[0], TradeDirection.Long));

        Assert.Empty(result.Trades);
        Assert.Equal(OptionCandidateRejection.MissingExitQuote, Assert.Single(result.RejectedCandidates).Reason);
    }

    private static OptionsBacktestResult Run(IReadOnlyList<Candle> candles, IReadOnlyList<OptionContract> contracts,
        IReadOnlyList<OptionQuote> quotes, StrategySignal signal) => OptionsBacktestEngine.Run(
            new FixedStrategy([signal]), candles, contracts, quotes, India, Settings());
    private static OptionsBacktestSettings Settings() => new() { AllowedRiskPerTrade = 100,
        MaximumCapitalPerTrade = 10_000, MaximumSpreadBasisPoints = 1000 };
    private static Candle[] Candles() => [Bar(0, 104), Bar(5, 105), Bar(10, 106)];
    private static Candle Bar(int minutes, decimal open, int day = 0) => new(Underlying, Timeframe.Minute5,
        Start.AddDays(day).AddMinutes(minutes), open, open + 1, open - 1, open, 1000);
    private static OptionContract Contract(decimal strike, OptionRight right, int suffix, int expiryDays = 2) =>
        new(Guid.Parse($"{suffix:D8}-1717-1717-1717-171717171717"), Underlying, "NFO", $"OPT{suffix}",
            DateOnly.FromDateTime(Start.Date).AddDays(expiryDays), strike, right, 50, .05m);
    private static OptionQuote Quote(OptionContract contract, int minutes, decimal bid, decimal ask) =>
        new(contract.Id, Start.AddMinutes(minutes), bid, ask, (bid + ask) / 2, 1000, 1000);
    private static StrategySignal Signal(Candle candle, TradeDirection direction) => new("fixed", Underlying,
        candle.OpenTimeUtc, direction, candle.Close, candle.Close - 1, candle.Close + 3, 1, 3,
        new(1, 1, 1, 1, 25, 20, 10, 100, 80, candle.Close));

    private sealed class FixedStrategy(IReadOnlyList<StrategySignal> signals) : ITradingStrategy
    {
        public string Id => "fixed";
        public string Name => "Fixed";
        public IReadOnlyList<StrategySignal> Evaluate(IReadOnlyList<Candle> candles, TimeZoneInfo exchangeTimeZone) => signals;
    }
}
