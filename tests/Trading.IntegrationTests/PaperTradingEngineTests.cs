using Trading.Application.MarketData;
using Trading.Domain.MarketData;
using Trading.Execution.Paper;
using Trading.Risk.Policy;

namespace Trading.IntegrationTests;

public sealed class PaperTradingEngineTests
{
    private static readonly DateTime Entry = new(2026, 9, 15, 3, 45, 0, DateTimeKind.Utc);
    private static readonly Guid InstrumentId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void Approved_long_order_uses_ask_bid_whole_lots_fees_and_target_exit()
    {
        var result = PaperTradingEngine.Run(Request(false,
            [Tick(Entry, 99, 100), Tick(Entry.AddSeconds(1), 110, 111)]), new(), Calendar());

        var trade = Assert.Single(result.Trades);
        Assert.Equal(PaperOrderStatus.FilledAndClosed, trade.Status);
        Assert.Equal(PaperExitReason.Target, trade.ExitReason);
        Assert.Equal(50, trade.Quantity);
        Assert.Equal(2, trade.Lots);
        Assert.Equal(100m, trade.EntryPrice);
        Assert.Equal(110m, trade.ExitPrice);
        Assert.Equal(PaperPriceSource.BestAsk, trade.EntryPriceSource);
        Assert.Equal(PaperPriceSource.BestBid, trade.ExitPriceSource);
        Assert.Equal(PaperQuoteQualityPolicy.RequireBestBidAsk, result.QuoteQualityPolicy);
        Assert.Equal(500m, trade.GrossPnl);
        Assert.Equal(20m, trade.Fees);
        Assert.Equal(480m, result.RealizedNetPnl);
        Assert.Equal(30_480m, result.EndingCash);
        Assert.Equal(64, trade.RiskDecisionSha256!.Length);
    }

    [Fact]
    public void Ltp_above_target_does_not_trigger_while_executable_bid_is_below_target()
    {
        var trade = Execute([Tick(Entry, 99, 100, 100), Tick(Entry.AddSeconds(1), 104, 105, 110)]);
        Assert.Equal(PaperExitReason.PlannedExit, trade.ExitReason);
        Assert.Equal(104m, trade.ExitPrice);
    }

    [Fact]
    public void Executable_bid_at_target_triggers_even_when_ltp_is_below_target()
    {
        var trade = Execute([Tick(Entry, 99, 100, 100), Tick(Entry.AddSeconds(1), 105, 106, 104)]);
        Assert.Equal(PaperExitReason.Target, trade.ExitReason);
        Assert.Equal(105m, trade.ExitPrice);
    }

    [Fact]
    public void Ltp_below_stop_does_not_trigger_while_executable_bid_is_above_stop()
    {
        var trade = Execute([Tick(Entry, 99, 100, 100), Tick(Entry.AddSeconds(1), 95, 96, 80)]);
        Assert.Equal(PaperExitReason.PlannedExit, trade.ExitReason);
        Assert.Equal(95m, trade.ExitPrice);
    }

    [Fact]
    public void Executable_bid_at_stop_triggers_even_when_ltp_is_above_stop()
    {
        var trade = Execute([Tick(Entry, 99, 100, 100), Tick(Entry.AddSeconds(1), 90, 91, 95)]);
        Assert.Equal(PaperExitReason.Stop, trade.ExitReason);
        Assert.Equal(90m, trade.ExitPrice);
    }

    [Fact]
    public void Entry_uses_ask_and_planned_exit_uses_bid()
    {
        var trade = Execute([Tick(Entry, 98, 101, 99), Tick(Entry.AddSeconds(1), 103, 106, 100)]);
        Assert.Equal(101m, trade.EntryPrice);
        Assert.Equal(103m, trade.ExitPrice);
        Assert.Equal(PaperPriceSource.BestAsk, trade.EntryPriceSource);
        Assert.Equal(PaperPriceSource.BestBid, trade.ExitPriceSource);
    }

    [Fact]
    public void Entry_and_exit_slippage_are_both_adverse_to_the_long_position()
    {
        var result = PaperTradingEngine.Run(Request(false,
            [Tick(Entry, 99, 100, 100), Tick(Entry.AddSeconds(1), 110, 111, 110)], 100m),
            new(), Calendar());
        var trade = Assert.Single(result.Trades);
        Assert.Equal(101m, trade.EntryPrice);
        Assert.Equal(108.9m, trade.ExitPrice);
        Assert.Equal(395m, trade.GrossPnl);
    }

    [Fact]
    public void Bid_ask_spread_changes_realized_paper_pnl()
    {
        var narrow = Execute([Tick(Entry, 99, 100, 100), Tick(Entry.AddSeconds(1), 100, 101, 100)]);
        var wide = Execute([Tick(Entry, 98, 102, 100), Tick(Entry.AddSeconds(1), 98, 102, 100)]);
        Assert.Equal(0m, narrow.GrossPnl);
        Assert.Equal(-200m, wide.GrossPnl);
    }

    [Fact]
    public void Ltp_fallback_is_explicit_and_artifacted_or_missing_quotes_fail_closed()
    {
        var ticks = new[] { Tick(Entry, null, null, 100), Tick(Entry.AddSeconds(1), null, null, 105) };
        var rejected = PaperTradingEngine.Run(Request(false, ticks), new(), Calendar());
        Assert.Equal(PaperOrderStatus.DataRejected, Assert.Single(rejected.Trades).Status);

        var fallbackRequest = Request(false, ticks) with
        {
            QuoteQualityPolicy = PaperQuoteQualityPolicy.AllowLastPriceFallback
        };
        var fallback = PaperTradingEngine.Run(fallbackRequest, new(), Calendar());
        var trade = Assert.Single(fallback.Trades);
        Assert.Equal(PaperOrderStatus.FilledAndClosed, trade.Status);
        Assert.Equal(PaperPriceSource.LastPriceFallback, trade.EntryPriceSource);
        Assert.Equal(PaperPriceSource.LastPriceFallback, trade.ExitPriceSource);
        Assert.Equal(PaperQuoteQualityPolicy.AllowLastPriceFallback, fallback.QuoteQualityPolicy);
    }

    [Fact]
    public void Kill_switch_is_preserved_as_a_risk_rejection()
    {
        var result = PaperTradingEngine.Run(Request(true,
            [Tick(Entry, 99, 100), Tick(Entry.AddSeconds(1), 110, 111)]), new(), Calendar());
        var trade = Assert.Single(result.Trades);
        Assert.Equal(PaperOrderStatus.RiskRejected, trade.Status);
        Assert.Contains(RiskRejectionCode.KillSwitchEngaged, trade.RiskRejectionCodes);
        Assert.Equal(0, result.FilledTrades);
        Assert.Equal(30_000m, result.EndingCash);
    }

    [Fact]
    public void Stale_entry_data_is_rejected_before_risk_or_fill()
    {
        var result = PaperTradingEngine.Run(Request(false,
            [Tick(Entry.AddSeconds(10), 99, 100), Tick(Entry.AddSeconds(11), 110, 111)]), new(), Calendar());
        var trade = Assert.Single(result.Trades);
        Assert.Equal(PaperOrderStatus.DataRejected, trade.Status);
        Assert.Contains("fresh entry", trade.Rejection);
        Assert.Null(trade.EntryPrice);
        Assert.Null(trade.RiskDecisionSha256);
    }

    [Fact]
    public void Rebuilt_ledger_enforces_the_daily_trade_limit()
    {
        var orders = Enumerable.Range(0, 4).Select(index => new PaperTradeIntent(Guid.NewGuid(), InstrumentId,
            12345, "NFO", "TESTCE", Entry.AddSeconds(index * 10), Entry.AddSeconds(index * 10 + 2),
            90, 105, 25, 1)).ToArray();
        var ticks = Enumerable.Range(0, 4).SelectMany(index => new[]
        {
            Tick(Entry.AddSeconds(index * 10), 99, 100),
            Tick(Entry.AddSeconds(index * 10 + 1), 110, 111)
        }).ToArray();
        var result = PaperTradingEngine.Run(new(Guid.NewGuid(), Entry, "strategy-v1", 30_000,
            0, 0, 10, 5, false, orders, ticks), new(), Calendar());

        Assert.Equal(3, result.FilledTrades);
        var rejected = Assert.Single(result.Trades, item => item.Status == PaperOrderStatus.RiskRejected);
        Assert.Contains(RiskRejectionCode.DailyTradeLimitReached, rejected.RiskRejectionCodes);
    }

    private static PaperTradeResult Execute(IReadOnlyList<NormalizedMarketTick> ticks) =>
        Assert.Single(PaperTradingEngine.Run(Request(false, ticks), new(), Calendar()).Trades);
    private static PaperTradingRequest Request(bool killSwitch, IReadOnlyList<NormalizedMarketTick> ticks,
        decimal slippageBasisPoints = 0) =>
        new(Guid.Parse("22222222-2222-2222-2222-222222222221"), Entry, "strategy-v1", 30_000,
            slippageBasisPoints, 0, 10, 5, killSwitch,
            [new(Guid.Parse("22222222-2222-2222-2222-222222222223"), InstrumentId, 12345,
                "NFO", "TESTCE", Entry, Entry.AddSeconds(2), 90, 105, 25, 2)], ticks);

    private static NormalizedMarketTick Tick(DateTime time, decimal bid, decimal last) =>
        Tick(time, bid, bid + 1, last);
    private static NormalizedMarketTick Tick(DateTime time, decimal? bid, decimal? ask, decimal last) =>
        new(MarketFeedSource.PaperReplay, MarketFeedQuoteMode.Full, 12345, "NFO", "TESTCE", time,
            time, last, 25, last, 1000, 500, 500, 95, 112, 90, 96, 1000, bid, ask);
    private static ExchangeSessionCalendar Calendar() => new("nse-test",
        TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"), new(9, 15), new(15, 30),
        new HashSet<DateOnly>(), new Dictionary<DateOnly, ExchangeSession>());
}
