using Trading.Application.MarketData;
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
            [Tick(Entry, 99, 100), Tick(Entry.AddSeconds(1), 110, 111)]), new(), India());

        var trade = Assert.Single(result.Trades);
        Assert.Equal(PaperOrderStatus.FilledAndClosed, trade.Status);
        Assert.Equal(PaperExitReason.Target, trade.ExitReason);
        Assert.Equal(50, trade.Quantity);
        Assert.Equal(2, trade.Lots);
        Assert.Equal(100m, trade.EntryPrice);
        Assert.Equal(110m, trade.ExitPrice);
        Assert.Equal(500m, trade.GrossPnl);
        Assert.Equal(20m, trade.Fees);
        Assert.Equal(480m, result.RealizedNetPnl);
        Assert.Equal(30_480m, result.EndingCash);
        Assert.Equal(64, trade.RiskDecisionSha256!.Length);
    }

    [Fact]
    public void Kill_switch_is_preserved_as_a_risk_rejection()
    {
        var result = PaperTradingEngine.Run(Request(true,
            [Tick(Entry, 99, 100), Tick(Entry.AddSeconds(1), 110, 111)]), new(), India());
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
            [Tick(Entry.AddSeconds(10), 99, 100), Tick(Entry.AddSeconds(11), 110, 111)]), new(), India());
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
            0, 0, 10, 5, false, orders, ticks), new(), India());

        Assert.Equal(3, result.FilledTrades);
        var rejected = Assert.Single(result.Trades, item => item.Status == PaperOrderStatus.RiskRejected);
        Assert.Contains(RiskRejectionCode.DailyTradeLimitReached, rejected.RiskRejectionCodes);
    }

    private static PaperTradingRequest Request(bool killSwitch, IReadOnlyList<NormalizedMarketTick> ticks) =>
        new(Guid.Parse("22222222-2222-2222-2222-222222222221"), Entry, "strategy-v1", 30_000,
            0, 0, 10, 5, killSwitch,
            [new(Guid.Parse("22222222-2222-2222-2222-222222222223"), InstrumentId, 12345,
                "NFO", "TESTCE", Entry, Entry.AddSeconds(2), 90, 105, 25, 2)], ticks);

    private static NormalizedMarketTick Tick(DateTime time, decimal bid, decimal last) =>
        new(MarketFeedSource.PaperReplay, MarketFeedQuoteMode.Full, 12345, "NFO", "TESTCE", time,
            time, last, 25, last, 1000, 500, 500, 95, 112, 90, 96, 1000, bid, bid + 1);
    private static TimeZoneInfo India() => TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
}
