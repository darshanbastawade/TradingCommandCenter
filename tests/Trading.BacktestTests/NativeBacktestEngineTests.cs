using Trading.Application.Backtesting;
using Trading.Application.MarketData;
using Trading.Backtesting.Engines;
using Trading.Domain.MarketData;

namespace Trading.BacktestTests;

public sealed class NativeBacktestEngineTests
{
    private static readonly Guid InstrumentId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTime From = new(2026, 9, 1, 3, 45, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Authoritative_adapter_translates_specification_and_hashes_consumed_data()
    {
        var store = new FakeMarketDataStore(Instrument(), Candles());
        var engine = new NativeBacktestEngine(store);
        var specification = BacktestSpecificationCodec.Seal(Specification());

        var first = await engine.RunAsync(specification);
        var second = await engine.RunAsync(specification);

        Assert.Equal("native-csharp", first.EngineId);
        Assert.Equal(BacktestEngineRole.Authoritative, first.EngineRole);
        Assert.Equal(specification.SpecificationSha256, first.SpecificationSha256);
        Assert.Equal(new string('a', 64), first.DeclaredDatasetSha256);
        Assert.Equal(first.ConsumedMarketDataSha256, second.ConsumedMarketDataSha256);
        Assert.Equal(first.ResultSha256, second.ResultSha256);
        Assert.True(BacktestRunCodec.Verify(first));
        Assert.Empty(first.Trades);
    }

    [Fact]
    public async Task Adapter_rejects_tampered_specification_and_instrument_mismatch()
    {
        var specification = BacktestSpecificationCodec.Seal(Specification());
        var engine = new NativeBacktestEngine(new FakeMarketDataStore(Instrument(), Candles()));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.RunAsync(specification with
        { SpecificationSha256 = new string('b', 64) }));

        var mismatched = new Instrument(InstrumentId, "BSE", "NIFTY 50", "Fixture", 1, .05m);
        var mismatchEngine = new NativeBacktestEngine(new FakeMarketDataStore(mismatched, Candles()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => mismatchEngine.RunAsync(specification));
    }

    [Fact]
    public async Task Adapter_requires_complete_strategy_parameters_and_supported_data_mode()
    {
        var engine = new NativeBacktestEngine(new FakeMarketDataStore(Instrument(), Candles()));
        var missing = Specification() with
        { Parameters = Specification().Parameters.Where(item => item.Key != "minimumAdx")
            .ToDictionary(item => item.Key, item => item.Value) };
        await Assert.ThrowsAsync<ArgumentException>(() => engine.RunAsync(BacktestSpecificationCodec.Seal(missing)));

        var option = Specification() with
        {
            Data = Specification().Data with { MarketDataMode = BacktestMarketDataMode.ObservedOptionQuotes },
            Execution = Specification().Execution with { EntryFillPolicy = BacktestEntryFillPolicy.ObservedAsk },
            Instrument = Specification().Instrument with { AssetClass = BacktestAssetClass.IndexOption }
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => engine.RunAsync(BacktestSpecificationCodec.Seal(option)));

        var wrongCosts = Specification() with
        {
            Execution = Specification().Execution with
            {
                CostProfileId = "zerodha-nse-equity-options-2026-04-01"
            }
        };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            engine.RunAsync(BacktestSpecificationCodec.Seal(wrongCosts)));
    }

    [Theory]
    [InlineData("vwap-ema-trend-breakout-v1")]
    [InlineData("opening-range-breakout-v1")]
    [InlineData("ema-pullback-continuation-v1")]
    [InlineData("vwap-reclaim-rejection-v1")]
    [InlineData("adx-trend-continuation-v1")]
    public async Task Adapter_supports_each_registered_native_strategy(string strategyId)
    {
        var engine = new NativeBacktestEngine(new FakeMarketDataStore(Instrument(), Candles()));
        var run = await engine.RunAsync(BacktestSpecificationCodec.Seal(
            Specification() with { StrategyId = strategyId, Parameters = Parameters(strategyId) }));

        Assert.True(BacktestRunCodec.Verify(run));
    }

    private static BacktestSpecification Specification() => new(1, "vwap-ema-trend-breakout-v1",
        new(InstrumentId, "NSE", "NIFTY 50", BacktestAssetClass.EquityIndex, "INR", 1, .05m),
        new(From, From.AddMinutes(15), 5, "India Standard Time", "nse-v1", "fixture", "v1",
            new string('a', 64), BacktestMarketDataMode.OhlcvBars),
        new(100_000, 750, 30_000, 5),
        new(3, 0, "none", new TimeOnly(15, 25), BacktestSignalTiming.CompletedBar,
            BacktestEntryFillPolicy.NextObservedBarOpen, BacktestAmbiguousBarPolicy.StopFirst,
            BacktestEndOfDataPolicy.CloseLastObserved),
        new Dictionary<string, decimal>
        {
            ["fastEmaPeriod"] = 20, ["slowEmaPeriod"] = 50, ["atrPeriod"] = 14,
            ["adxPeriod"] = 14, ["volumeAveragePeriod"] = 20, ["breakoutLookbackBars"] = 3,
            ["minimumAdx"] = 25, ["volumeMultiplier"] = 1.2m, ["atrStopMultiple"] = 1,
            ["rewardRiskMultiple"] = 3, ["entryWindowStartMinuteOfDay"] = 570,
            ["entryWindowEndMinuteOfDay"] = 690
        });

    private static IReadOnlyDictionary<string, decimal> Parameters(string strategyId)
    {
        var values = new Dictionary<string, decimal>
        {
            ["fastEmaPeriod"] = 20, ["slowEmaPeriod"] = 50, ["atrPeriod"] = 14,
            ["adxPeriod"] = 14, ["volumeAveragePeriod"] = 20, ["minimumAdx"] = 25,
            ["volumeMultiplier"] = 1.2m, ["atrStopMultiple"] = 1,
            ["rewardRiskMultiple"] = 3, ["entryWindowStartMinuteOfDay"] = 570,
            ["entryWindowEndMinuteOfDay"] = 690
        };
        if (strategyId == "vwap-ema-trend-breakout-v1") values["breakoutLookbackBars"] = 3;
        if (strategyId == "opening-range-breakout-v1")
        {
            values.Remove("minimumAdx");
            values["openingRangeBars"] = 3;
        }
        if (strategyId == "adx-trend-continuation-v1") values.Remove("volumeMultiplier");
        return values;
    }

    private static Instrument Instrument() => new(InstrumentId, "NSE", "NIFTY 50", "Fixture", 1, .05m);
    private static IReadOnlyList<Candle> Candles() =>
    [
        new(InstrumentId, Timeframe.Minute5, From, 100, 101, 99, 100, 100),
        new(InstrumentId, Timeframe.Minute5, From.AddMinutes(5), 100, 101, 99, 100, 100),
        new(InstrumentId, Timeframe.Minute5, From.AddMinutes(10), 100, 101, 99, 100, 100)
    ];

    private sealed class FakeMarketDataStore(Instrument instrument, IReadOnlyList<Candle> candles) : IMarketDataStore
    {
        public Task AddInstrumentAsync(Instrument value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Instrument?> FindInstrumentAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Instrument?>(id == instrument.Id ? instrument : null);
        public Task AddCandlesAsync(IReadOnlyCollection<Candle> values, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<Candle>> ReadCandlesAsync(Guid instrumentId, Timeframe timeframe,
            DateTimeOffset from, DateTimeOffset to, int limit = 10000, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Candle>>(candles.Where(item => item.InstrumentId == instrumentId &&
                item.Timeframe == timeframe && item.OpenTimeUtc >= from.UtcDateTime && item.OpenTimeUtc < to.UtcDateTime)
                .Take(limit).ToArray());
    }
}
