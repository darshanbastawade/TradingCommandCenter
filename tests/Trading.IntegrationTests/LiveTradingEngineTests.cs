using Trading.Application.Execution;
using Trading.Domain.MarketData;
using Trading.Execution.Live;
using Trading.Risk.Policy;

namespace Trading.IntegrationTests;

public sealed class LiveTradingEngineTests
{
    private static readonly DateTime Now = new(2026, 9, 15, 3, 45, 0, DateTimeKind.Utc);

    [Fact]
    public void Fresh_flat_account_and_guarded_limit_produce_M18_sized_proposal()
    {
        var result = LiveTradingEngine.Prepare(new(), new() { KillSwitchEngaged = false }, Account(), Quote(), Intent(),
            "strategy-v1", Now, Calendar());
        Assert.Equal(50, result.Quantity);
        Assert.Equal(2, result.Lots);
        Assert.Equal(510m, result.InitialRisk);
        Assert.Equal(5_010m, result.RequiredCapital);
        Assert.Equal(64, result.RiskDecisionSha256.Length);
    }

    [Fact]
    public void Stale_snapshot_open_position_and_price_drift_fail_closed()
    {
        var settings = new LiveTradingSettings { KillSwitchEngaged = false };
        Assert.Throws<InvalidOperationException>(() => LiveTradingEngine.Prepare(new(), settings,
            Account() with { AsOfUtc = Now.AddSeconds(-6) }, Quote(), Intent(), "strategy-v1", Now, Calendar()));
        Assert.Throws<InvalidOperationException>(() => LiveTradingEngine.Prepare(new(), settings,
            Account() with { Positions = [new(12345, "NFO", "TESTCE", "MIS", 25)] }, Quote(),
            Intent(), "strategy-v1", Now, Calendar()));
        Assert.Throws<InvalidOperationException>(() => LiveTradingEngine.Prepare(new(), settings,
            Account() with { DayOrderCount = 1 }, Quote(), Intent(), "strategy-v1", Now, Calendar()));
        Assert.Throws<InvalidOperationException>(() => LiveTradingEngine.Prepare(new(), settings,
            Account(), Quote(), Intent() with { LimitPrice = 101 }, "strategy-v1", Now, Calendar()));
    }

    private static BrokerAccountSnapshot Account() => new(Now, 30_000, [], 0);
    private static BrokerQuote Quote() => new(Now, 12345, "NFO", "TESTCE", 100, 99.9m, 100m);
    private static LiveEntryIntent Intent() => new(Guid.NewGuid(), Guid.NewGuid(), 12345, "NFO", "TESTCE",
        Now, Now.AddMinutes(1), 100.2m, 90, 110, 25, 2, true);
    private static ExchangeSessionCalendar Calendar() => new("nse-test",
        TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"), new(9, 15), new(15, 30),
        new HashSet<DateOnly>(), new Dictionary<DateOnly, ExchangeSession>());
}
