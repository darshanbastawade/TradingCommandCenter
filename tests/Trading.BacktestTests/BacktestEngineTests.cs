using Trading.Backtesting;
using Trading.Backtesting.Costs;
using Trading.Domain.MarketData;
using Trading.Strategies.Contracts;

namespace Trading.BacktestTests;

public sealed class BacktestEngineTests
{
    private static readonly Guid InstrumentId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T09:30:00+05:30");
    private static readonly TimeZoneInfo India = TimeZoneInfo.CreateCustomTimeZone(
        "Backtest India", TimeSpan.FromMinutes(330), "Backtest India", "Backtest India");

    [Fact]
    public void Long_candidate_enters_next_bar_and_exits_at_target()
    {
        var candles = new[] { Bar(0, 99, 100, 98, 99), Bar(5, 100, 103, 100, 102) };
        var result = Run(candles, Signal(candles[0], TradeDirection.Long));

        var trade = Assert.Single(result.Trades);
        Assert.Equal(candles[1].OpenTimeUtc, trade.EntryBarOpenTimeUtc);
        Assert.Equal(100m, trade.EntryPrice);
        Assert.Equal(99m, trade.StopPrice);
        Assert.Equal(103m, trade.TargetPrice);
        Assert.Equal(103m, trade.ExitPrice);
        Assert.Equal(BacktestExitReason.Target, trade.ExitReason);
        Assert.Equal(3m, trade.NetPnl);
        Assert.Equal(100_003m, result.FinalCapital);
    }

    [Fact]
    public void Short_candidate_enters_next_bar_and_exits_at_target()
    {
        var candles = new[] { Bar(0, 101, 102, 100, 101), Bar(5, 100, 100, 97, 98) };
        var trade = Assert.Single(Run(candles, Signal(candles[0], TradeDirection.Short)).Trades);

        Assert.Equal(101m, trade.StopPrice);
        Assert.Equal(97m, trade.TargetPrice);
        Assert.Equal(97m, trade.ExitPrice);
        Assert.Equal(3m, trade.GrossPnl);
    }

    [Fact]
    public void Stop_wins_when_stop_and_target_are_both_inside_one_candle()
    {
        var candles = new[] { Bar(0, 99, 100, 98, 99), Bar(5, 100, 104, 98, 101) };
        var trade = Assert.Single(Run(candles, Signal(candles[0], TradeDirection.Long)).Trades);

        Assert.Equal(BacktestExitReason.StopLoss, trade.ExitReason);
        Assert.Equal(99m, trade.ExitPrice);
        Assert.Equal(-1m, trade.NetPnl);
    }

    [Fact]
    public void Gap_through_stop_exits_at_gap_open()
    {
        var candles = new[]
        {
            Bar(0, 99, 100, 98, 99),
            Bar(5, 100, 100, 99.5m, 99.5m),
            Bar(10, 97, 98, 96, 97)
        };
        var trade = Assert.Single(Run(candles, Signal(candles[0], TradeDirection.Long)).Trades);

        Assert.Equal(BacktestExitReason.StopLoss, trade.ExitReason);
        Assert.Equal(97m, trade.ExitPrice);
        Assert.Equal(-3m, trade.GrossPnl);
    }

    [Fact]
    public void Slippage_and_costs_are_adverse_and_auditable()
    {
        var candles = new[] { Bar(0, 99, 100, 98, 99), Bar(5, 100, 104, 100, 103) };
        var settings = new BacktestSettings
        {
            Quantity = 2,
            SlippageBasisPointsPerSide = 10,
            VariableCostBasisPointsPerSide = 10,
            FixedCostPerSide = 1
        };
        var trade = Assert.Single(Run(candles, Signal(candles[0], TradeDirection.Long), settings).Trades);

        Assert.Equal(100.1m, trade.EntryPrice);
        Assert.Equal(103.1m, trade.TargetPrice);
        Assert.Equal(102.9969m, trade.ExitPrice);
        Assert.Equal(5.7938m, trade.GrossPnl);
        Assert.Equal(2.4061938m, trade.Costs);
        Assert.Equal(3.3876062m, trade.NetPnl);
    }

    [Fact]
    public void Risk_sizing_and_named_cost_profile_flow_into_trade_ledger()
    {
        var candles = new[] { Bar(0, 99, 100, 98, 99), Bar(5, 100, 103, 100, 102) };
        var settings = new BacktestSettings
        {
            RiskBasedSizing = new(250m, LotSize: 50, MaximumCapitalPerTrade: 10_000m),
            CostModel = IndianCostProfiles.ZerodhaNseEquityOptions2026()
        };
        var trade = Assert.Single(Run(candles, Signal(candles[0], TradeDirection.Long), settings).Trades);

        Assert.Equal(100, trade.Quantity);
        Assert.Equal(300m, trade.GrossPnl);
        Assert.Equal(15m, trade.CostBreakdown.SecuritiesTransactionTax);
        Assert.Equal(71.0348102m, trade.Costs);
        Assert.Equal(228.9651898m, trade.NetPnl);
    }

    [Fact]
    public void Candidate_is_ignored_when_risk_budget_cannot_buy_one_lot()
    {
        var candles = new[] { Bar(0, 99, 100, 98, 99), Bar(5, 100, 103, 100, 102) };
        var settings = new BacktestSettings
        {
            RiskBasedSizing = new(10m, LotSize: 50, MaximumCapitalPerTrade: 10_000m)
        };
        var result = Run(candles, Signal(candles[0], TradeDirection.Long), settings);

        Assert.Empty(result.Trades);
        Assert.Equal(IgnoredCandidateReason.InsufficientRiskOrCapital,
            Assert.Single(result.IgnoredCandidates).Reason);
    }

    [Fact]
    public void Open_position_exits_at_configured_session_time()
    {
        var start = DateTimeOffset.Parse("2026-09-01T15:15:00+05:30");
        var candles = new[]
        {
            Bar(start, 99, 100, 98, 99),
            Bar(start.AddMinutes(5), 100, 101, 100, 100),
            Bar(start.AddMinutes(10), 101, 102, 100, 101)
        };
        var trade = Assert.Single(Run(candles, Signal(candles[0], TradeDirection.Long)).Trades);

        Assert.Equal(BacktestExitReason.SessionExit, trade.ExitReason);
        Assert.Equal(101m, trade.ExitPrice);
        Assert.Equal(candles[2].OpenTimeUtc, trade.ExitBarOpenTimeUtc);
    }

    [Fact]
    public void Remaining_position_is_closed_at_end_of_data()
    {
        var candles = new[] { Bar(0, 99, 100, 98, 99), Bar(5, 100, 101, 100, 101) };
        var trade = Assert.Single(Run(candles, Signal(candles[0], TradeDirection.Long)).Trades);

        Assert.Equal(BacktestExitReason.EndOfData, trade.ExitReason);
        Assert.Equal(101m, trade.ExitPrice);
    }

    [Fact]
    public void Candidate_is_ignored_while_a_position_remains_open()
    {
        var candles = new[]
        {
            Bar(0, 99, 100, 98, 99),
            Bar(5, 100, 101, 100, 100),
            Bar(10, 100, 101, 100, 100)
        };
        var result = Run(candles, Signal(candles[0], TradeDirection.Long), Signal(candles[1], TradeDirection.Long));

        Assert.Single(result.Trades);
        Assert.Equal(IgnoredCandidateReason.PositionAlreadyOpen, Assert.Single(result.IgnoredCandidates).Reason);
    }

    [Fact]
    public void Candidate_cannot_enter_on_another_session_or_after_final_bar()
    {
        var first = Bar(0, 99, 100, 98, 99);
        var nextDay = Bar(Start.AddDays(1), 100, 101, 99, 100);
        var sessionResult = Run([first, nextDay], Signal(first, TradeDirection.Long));
        Assert.Equal(IgnoredCandidateReason.FollowingBarInDifferentSession,
            Assert.Single(sessionResult.IgnoredCandidates).Reason);

        var finalResult = Run([first], Signal(first, TradeDirection.Long));
        Assert.Equal(IgnoredCandidateReason.NoFollowingBar, Assert.Single(finalResult.IgnoredCandidates).Reason);
    }

    [Fact]
    public void Candidate_is_ignored_when_next_bar_cannot_support_positive_levels()
    {
        var candles = new[] { Bar(0, 99, 100, 98, 99), Bar(5, 50, 51, 49, 50) };
        var signal = Signal(candles[0], TradeDirection.Long) with { RiskPerUnit = 100 };
        var result = Run(candles, signal);

        Assert.Empty(result.Trades);
        Assert.Equal(IgnoredCandidateReason.InvalidEntryLevels, Assert.Single(result.IgnoredCandidates).Reason);
    }

    [Fact]
    public void Invalid_settings_candles_and_strategy_output_are_rejected()
    {
        var candle = Bar(0, 99, 100, 98, 99);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Run([candle], settings: new BacktestSettings { Quantity = 0 }));
        Assert.Throws<ArgumentException>(() =>
            Run([candle, Bar(-5, 99, 100, 98, 99)]));
        Assert.Throws<InvalidOperationException>(() =>
            Run([candle], Signal(candle, TradeDirection.Long) with { RiskPerUnit = 0 }));
    }

    private static BacktestResult Run(IReadOnlyList<Candle> candles, params StrategySignal[] signals) =>
        Run(candles, signals, new());

    private static BacktestResult Run(IReadOnlyList<Candle> candles, StrategySignal signal, BacktestSettings settings) =>
        Run(candles, [signal], settings);

    private static BacktestResult Run(IReadOnlyList<Candle> candles, BacktestSettings settings) =>
        Run(candles, [], settings);

    private static BacktestResult Run(IReadOnlyList<Candle> candles, IReadOnlyList<StrategySignal> signals,
        BacktestSettings settings) => BacktestEngine.Run(new FixedStrategy(signals), candles, India, settings);

    private static StrategySignal Signal(Candle candle, TradeDirection direction) =>
        new("test-strategy", InstrumentId, candle.OpenTimeUtc, direction, candle.Close,
            direction == TradeDirection.Long ? candle.Close - 1 : candle.Close + 1,
            direction == TradeDirection.Long ? candle.Close + 3 : candle.Close - 3,
            1, 3, new(1, 1, 1, 1, 25, 20, 10, 100, 80, candle.Close));

    private static Candle Bar(int minutes, decimal open, decimal high, decimal low, decimal close) =>
        Bar(Start.AddMinutes(minutes), open, high, low, close);

    private static Candle Bar(DateTimeOffset time, decimal open, decimal high, decimal low, decimal close) =>
        new(InstrumentId, Timeframe.Minute5, time, open, high, low, close, 100);

    private sealed class FixedStrategy(IReadOnlyList<StrategySignal> signals) : ITradingStrategy
    {
        public string Id => "test-strategy";
        public string Name => "Test strategy";
        public IReadOnlyList<StrategySignal> Evaluate(IReadOnlyList<Candle> candles, TimeZoneInfo exchangeTimeZone) => signals;
    }
}
