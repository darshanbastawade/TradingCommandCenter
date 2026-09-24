using Trading.Domain.MarketData;
using Trading.Risk.Policy;

namespace Trading.BacktestTests;

public sealed class DeterministicRiskPolicyTests
{
    private static readonly TimeZoneInfo India = TimeZoneInfo.CreateCustomTimeZone(
        "M18 India", TimeSpan.FromMinutes(330), "M18", "M18");
    private static readonly Guid Instrument = Guid.Parse("18181818-1818-1818-1818-181818181818");
    private const string Strategy = "qualified-v1";

    [Fact]
    public void Default_policy_approves_a_qualified_intraday_trade_deterministically()
    {
        var request = Request();
        var first = Evaluate(State(), request);
        var second = Evaluate(State(), request);

        Assert.True(first.Approved);
        Assert.Equal(750m, first.MaximumApprovedRisk);
        Assert.Equal(100_000m, first.MaximumApprovedCapital);
        Assert.Equal(first.DecisionSha256, second.DecisionSha256);
        Assert.Equal(64, first.DecisionSha256.Length);
    }

    [Fact]
    public void Protected_reserve_and_cash_buffer_never_expand_a_trading_pool()
    {
        var request = Request() with { RequiredCapital = 100_001m };
        var decision = Evaluate(State(availableCash: 300_000m), request);

        Assert.False(decision.Approved);
        Assert.Equal(100_000m, decision.MaximumApprovedCapital);
        Assert.Contains(RiskRejectionCode.CapitalPoolLimitExceeded, decision.RejectionCodes);
    }

    [Fact]
    public void Daily_loss_reduces_the_next_trade_risk_instead_of_recovering_with_more_size()
    {
        var loss = Closed(-1_000m, At(2026, 9, 15, 9, 30), At(2026, 9, 15, 9, 45));
        var decision = Evaluate(State(closed: [loss]), Request());

        Assert.False(decision.Approved);
        Assert.Equal(500m, decision.MaximumApprovedRisk);
        Assert.Contains(RiskRejectionCode.ProposedRiskExceedsAvailableRisk, decision.RejectionCodes);
    }

    [Fact]
    public void Two_consecutive_losses_stop_the_rest_of_the_session()
    {
        var state = State(closed:
        [
            Closed(-500m, At(2026, 9, 15, 9, 30), At(2026, 9, 15, 9, 45)),
            Closed(-400m, At(2026, 9, 15, 10, 0), At(2026, 9, 15, 10, 15))
        ]);
        var decision = Evaluate(state, Request(entryHour: 11));

        Assert.False(decision.Approved);
        Assert.Equal(2, decision.ConsecutiveLossesToday);
        Assert.Contains(RiskRejectionCode.ConsecutiveLossLimitReached, decision.RejectionCodes);
    }

    [Fact]
    public void Daily_weekly_and_monthly_limits_use_exchange_local_exit_dates()
    {
        var trades = new[]
        {
            Closed(-1_500m, At(2026, 9, 15, 9, 30), At(2026, 9, 15, 9, 45)),
            Closed(-1_500m, At(2026, 9, 14, 9, 30), At(2026, 9, 14, 9, 45)),
            Closed(-3_000m, At(2026, 9, 8, 9, 30), At(2026, 9, 8, 9, 45))
        };
        var decision = Evaluate(State(closed: trades), Request(entryHour: 11));

        Assert.Contains(RiskRejectionCode.DailyLossLimitReached, decision.RejectionCodes);
        Assert.Contains(RiskRejectionCode.WeeklyLossLimitReached, decision.RejectionCodes);
        Assert.Contains(RiskRejectionCode.MonthlyLossLimitReached, decision.RejectionCodes);
        Assert.Equal(-6_000m, decision.Month.RealizedPnl);
    }

    [Fact]
    public void Trade_count_qualification_and_kill_switch_are_independent_hard_gates()
    {
        var trades = Enumerable.Range(0, 3).Select(index => Closed(100m,
            At(2026, 9, 15, 9 + index, 20), At(2026, 9, 15, 9 + index, 40))).ToArray();
        var state = new RiskPortfolioState(100_000m, true, new HashSet<string>(), trades, []);
        var decision = Evaluate(state, Request(entryHour: 13));

        Assert.Contains(RiskRejectionCode.KillSwitchEngaged, decision.RejectionCodes);
        Assert.Contains(RiskRejectionCode.StrategyNotQualified, decision.RejectionCodes);
        Assert.Contains(RiskRejectionCode.DailyTradeLimitReached, decision.RejectionCodes);
    }

    [Fact]
    public void Averaging_open_exposure_and_aggregate_risk_are_rejected()
    {
        var open = new OpenRiskPosition(Guid.NewGuid(), Strategy, Instrument,
            At(2026, 9, 15, 9, 30), 500m, 10_000m, TradingCapitalPool.Active);
        var decision = Evaluate(State(open: [open]), Request(entryHour: 11) with { IsScaleIn = true });

        Assert.Contains(RiskRejectionCode.AveragingForbidden, decision.RejectionCodes);
        Assert.Contains(RiskRejectionCode.MaximumOpenPositionsReached, decision.RejectionCodes);
        Assert.Contains(RiskRejectionCode.AggregateOpenRiskExceeded, decision.RejectionCodes);
    }

    [Theory]
    [InlineData(2026, 9, 19, 10, 0, 2026, 9, 19, 15, 20, RiskRejectionCode.OutsideTradingSession)]
    [InlineData(2026, 9, 15, 15, 0, 2026, 9, 15, 15, 20, RiskRejectionCode.OutsideTradingSession)]
    [InlineData(2026, 9, 15, 10, 0, 2026, 9, 16, 10, 0, RiskRejectionCode.OvernightPositionForbidden)]
    public void Session_and_overnight_rules_are_hard_gates(int ey, int em, int ed, int eh, int emin,
        int xy, int xm, int xd, int xh, int xmin, RiskRejectionCode expected)
    {
        var request = Request() with { ProposedEntryUtc = At(ey, em, ed, eh, emin),
            PlannedExitUtc = At(xy, xm, xd, xh, xmin) };
        Assert.Contains(expected, Evaluate(State(), request).RejectionCodes);
    }

    [Fact]
    public void Shared_calendar_accepts_normal_weekday_and_budget_Saturday_but_rejects_holiday_and_ordinary_Saturday()
    {
        var calendar = CanonicalCalendar();
        Assert.True(Evaluate(calendar, RequestAt(2025, 2, 3, 9, 15, 15, 25)).Approved);
        Assert.True(Evaluate(calendar, RequestAt(2025, 2, 1, 9, 15, 15, 25)).Approved);

        var holiday = Evaluate(calendar, RequestAt(2025, 2, 26, 10, 0, 15, 20));
        var saturday = Evaluate(calendar, RequestAt(2025, 2, 8, 10, 0, 15, 20));
        Assert.Contains(RiskRejectionCode.OutsideTradingSession, holiday.RejectionCodes);
        Assert.Contains(RiskRejectionCode.OutsideTradingSession, saturday.RejectionCodes);
    }

    [Fact]
    public void Muhurat_session_intersects_policy_window_and_honors_exact_boundaries()
    {
        var calendar = CanonicalCalendar();
        var beforeSpecialOpen = Evaluate(calendar, RequestAt(2025, 10, 21, 9, 15, 14, 30));
        var atSpecialOpen = Evaluate(calendar, RequestAt(2025, 10, 21, 13, 45, 14, 45));
        var atSpecialClose = Evaluate(calendar, RequestAt(2025, 10, 21, 14, 45, 14, 45));

        Assert.Contains(RiskRejectionCode.OutsideTradingSession, beforeSpecialOpen.RejectionCodes);
        Assert.True(atSpecialOpen.Approved);
        Assert.Contains(RiskRejectionCode.OutsideTradingSession, atSpecialClose.RejectionCodes);
    }

    [Fact]
    public void Early_or_special_session_close_caps_mandatory_exit()
    {
        var calendar = CanonicalCalendar();
        var afterMuhuratClose = Evaluate(calendar, RequestAt(2025, 10, 21, 13, 45, 14, 46));
        var afterEarlyClose = Evaluate(calendar, RequestAt(2025, 12, 24, 9, 15, 13, 1));

        Assert.Contains(RiskRejectionCode.OvernightPositionForbidden, afterMuhuratClose.RejectionCodes);
        Assert.Contains(RiskRejectionCode.OvernightPositionForbidden, afterEarlyClose.RejectionCodes);
    }

    [Fact]
    public void Normal_entry_policy_boundaries_are_start_inclusive_and_end_exclusive()
    {
        var calendar = CanonicalCalendar();
        Assert.True(Evaluate(calendar, RequestAt(2025, 2, 3, 9, 15, 15, 25)).Approved);
        Assert.Contains(RiskRejectionCode.OutsideTradingSession,
            Evaluate(calendar, RequestAt(2025, 2, 3, 15, 0, 15, 25)).RejectionCodes);
    }

    [Fact]
    public void Calendar_identity_and_rules_are_bound_into_decision_fingerprint()
    {
        var request = RequestAt(2025, 2, 3, 10, 0, 15, 20);
        var first = Evaluate(CanonicalCalendar(), request);
        var changed = new ExchangeSessionCalendar("nse-2025-v2", India, new(9, 15), new(15, 30),
            new HashSet<DateOnly> { new(2025, 2, 26), new(2025, 3, 14) },
            new Dictionary<DateOnly, ExchangeSession>
            {
                [new(2025, 2, 1)] = new(new(2025, 2, 1), new(9, 15), new(15, 30)),
                [new(2025, 10, 21)] = new(new(2025, 10, 21), new(13, 30), new(14, 45))
            });
        var second = Evaluate(changed, request);

        Assert.Equal(CanonicalCalendar().Sha256, first.CalendarSha256);
        Assert.NotEqual(first.CalendarSha256, second.CalendarSha256);
        Assert.NotEqual(first.DecisionSha256, second.DecisionSha256);
    }

    [Fact]
    public void Invalid_allocation_and_future_history_are_rejected()
    {
        var settings = new DeterministicRiskPolicySettings { Capital = new(TotalCapital: 299_999m) };
        Assert.Throws<ArgumentException>(() => DeterministicRiskPolicy.Evaluate(settings, State(), Request(), Calendar()));
        var future = Closed(10m, At(2026, 9, 15, 12, 0), At(2026, 9, 15, 12, 30));
        Assert.Throws<ArgumentException>(() => Evaluate(State(closed: [future]), Request(entryHour: 10)));
    }

    [Fact]
    public void Combined_decision_sizes_down_to_whole_lots_from_current_loss_capacity()
    {
        var state = State(closed: [Closed(-1_000m, At(2026, 9, 15, 9, 30), At(2026, 9, 15, 9, 45))]);
        var request = new TradeSizingRiskRequest(Guid.NewGuid(), Strategy, Instrument,
            At(2026, 9, 15, 10, 0), At(2026, 9, 15, 15, 20), 100m, 95m,
            25, null, TradingCapitalPool.Active);
        var decision = DeterministicRiskPolicy.EvaluateAndSize(new(), state, request, Calendar());

        Assert.True(decision.Approved);
        Assert.Equal(100, decision.PositionSize.Quantity);
        Assert.Equal(4, decision.PositionSize.Lots);
        Assert.Equal(500m, decision.PositionSize.TotalRisk);
        Assert.Equal(64, decision.DecisionSha256.Length);
    }

    [Fact]
    public void Combined_decision_never_rounds_up_when_one_lot_exceeds_available_risk()
    {
        var request = new TradeSizingRiskRequest(Guid.NewGuid(), Strategy, Instrument,
            At(2026, 9, 15, 10, 0), At(2026, 9, 15, 15, 20), 100m, 50m,
            25, null, TradingCapitalPool.Active);
        var decision = DeterministicRiskPolicy.EvaluateAndSize(new(), State(), request, Calendar());

        Assert.False(decision.Approved);
        Assert.False(decision.CanFundWholeLot);
        Assert.Equal(0, decision.PositionSize.Quantity);
    }

    private static RiskDecision Evaluate(RiskPortfolioState state, TradeRiskRequest request) =>
        DeterministicRiskPolicy.Evaluate(new(), state, request, Calendar());
    private static RiskDecision Evaluate(ExchangeSessionCalendar calendar, TradeRiskRequest request) =>
        DeterministicRiskPolicy.Evaluate(new(), State(), request, calendar);
    private static ExchangeSessionCalendar Calendar() => new("nse-test", India, new(9, 15), new(15, 30),
        new HashSet<DateOnly>(), new Dictionary<DateOnly, ExchangeSession>());
    private static RiskPortfolioState State(decimal availableCash = 100_000m,
        IReadOnlyList<ClosedRiskTrade>? closed = null, IReadOnlyList<OpenRiskPosition>? open = null) =>
        new(availableCash, false, new HashSet<string> { Strategy }, closed ?? [], open ?? []);
    private static TradeRiskRequest Request(int entryHour = 10) => new(Guid.Parse("aaaaaaaa-1818-1818-1818-181818181818"),
        Strategy, Instrument, At(2026, 9, 15, entryHour, 0), At(2026, 9, 15, 15, 20),
        750m, 10_000m, TradingCapitalPool.Active);
    private static TradeRiskRequest RequestAt(int year, int month, int day, int entryHour, int entryMinute,
        int exitHour, int exitMinute) => new(Guid.Parse("aaaaaaaa-1818-1818-1818-181818181818"),
        Strategy, Instrument, At(year, month, day, entryHour, entryMinute),
        At(year, month, day, exitHour, exitMinute), 750m, 10_000m, TradingCapitalPool.Active);
    private static ExchangeSessionCalendar CanonicalCalendar() => new("nse-2025-v2", India,
        new(9, 15), new(15, 30), new HashSet<DateOnly> { new(2025, 2, 26), new(2025, 3, 14) },
        new Dictionary<DateOnly, ExchangeSession>
        {
            [new(2025, 2, 1)] = new(new(2025, 2, 1), new(9, 15), new(15, 30)),
            [new(2025, 10, 21)] = new(new(2025, 10, 21), new(13, 45), new(14, 45)),
            [new(2025, 12, 24)] = new(new(2025, 12, 24), new(9, 15), new(13, 0))
        });
    private static ClosedRiskTrade Closed(decimal pnl, DateTime entry, DateTime exit) =>
        new(Guid.NewGuid(), Strategy, Instrument, entry, exit, 750m, pnl);
    private static DateTime At(int year, int month, int day, int hour, int minute) =>
        TimeZoneInfo.ConvertTimeToUtc(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified), India);
}
