using System.Text.Json;
using Trading.Application.Backtesting;
using Trading.Application.MarketData;
using Trading.Domain.MarketData;
using Trading.ExternalValidation.Lean;

namespace Trading.BacktestTests;

public sealed class LeanBacktestEngineTests
{
    private static readonly Guid InstrumentId = Guid.Parse("a1926531-5ddc-4bf9-b377-8094260b2999");
    private static readonly DateTime From = new(2026, 9, 1, 3, 45, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Adapter_passes_sealed_input_and_accepts_verified_lean_evidence()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tcc-lean-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var runner = new FixtureRunner();
            var engine = new LeanBacktestEngine(new Store(), runner, Options(directory));
            var specification = Specification();

            var run = await engine.RunAsync(specification);

            Assert.Equal(BacktestEngineRole.IndependentValidation, engine.Role);
            Assert.Equal("lean", run.EngineId);
            Assert.Equal(specification.SpecificationSha256, run.SpecificationSha256);
            Assert.True(BacktestRunCodec.Verify(run));
            Assert.True(BacktestRunCodec.VerifyExternalValidation(run.ExternalValidation));
            Assert.Equal("fixture-v1", run.ExternalValidation!.StrategyImplementationVersion);
            Assert.Equal(Options(directory).Image, run.ExternalValidation.LeanImage);
            Assert.Equal(2, runner.ObservedCandles);
            Assert.Empty(Directory.GetDirectories(Path.Combine(directory, "tcc-lean")));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Adapter_rejects_identity_mismatch_and_unsupported_option_mode()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tcc-lean-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var engine = new LeanBacktestEngine(new Store(), new FixtureRunner(tamperHash: true), Options(directory));
            await Assert.ThrowsAsync<InvalidDataException>(() => engine.RunAsync(Specification()));
            var option = Specification().Specification with
            {
                Instrument = Specification().Specification.Instrument with
                { AssetClass = BacktestAssetClass.IndexOption },
                Data = Specification().Specification.Data with
                { MarketDataMode = BacktestMarketDataMode.ObservedOptionQuotes },
                Execution = Specification().Specification.Execution with
                { EntryFillPolicy = BacktestEntryFillPolicy.ObservedAsk }
            };
            await Assert.ThrowsAsync<NotSupportedException>(() =>
                engine.RunAsync(BacktestSpecificationCodec.Seal(option)));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Adapter_rejects_portable_trade_that_disagrees_with_official_lean_trade()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tcc-lean-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var engine = new LeanBacktestEngine(new Store(), new FixtureRunner(tamperTrade: true), Options(directory));
            await Assert.ThrowsAsync<InvalidDataException>(() => engine.RunAsync(Specification()));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Process_runner_rejects_unpinned_image_before_starting_cli()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tcc-lean-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var runner = new LeanProcessRunner(new LeanOptions
            {
                ProjectDirectory = directory, Image = "quantconnect/lean:latest"
            });
            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(
                new LeanMappedInput("run", "request", "candles", "evidence", directory, "hash", 1,
                    "request-hash", "candle-hash", "vwap-ema-trend-breakout-v1", "source-hash",
                    "fixture-v1", "quantconnect/lean:latest"),
                CancellationToken.None));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Adapter_rejects_stale_algorithm_source_manifest()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tcc-lean-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var options = Options(directory);
            await File.AppendAllTextAsync(Path.Combine(options.ProjectDirectory, "main.py"), "# changed\n");
            var engine = new LeanBacktestEngine(new Store(), new FixtureRunner(), options);
            await Assert.ThrowsAsync<InvalidDataException>(() => engine.RunAsync(Specification()));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static SealedBacktestSpecification Specification() => BacktestSpecificationCodec.Seal(new(1,
        "vwap-ema-trend-breakout-v1",
        new(InstrumentId, "NSE", "NIFTY 50", BacktestAssetClass.EquityIndex, "INR", 1, .05m),
        new(From, From.AddMinutes(10), 5, "India Standard Time", "nse-v1", "fixture", "v1",
            new string('a', 64), BacktestMarketDataMode.OhlcvBars),
        new(100_000, 750, 30_000, 5),
        new(3, 0, "none", new TimeOnly(15, 25), BacktestSignalTiming.CompletedBar,
            BacktestEntryFillPolicy.NextObservedBarOpen, BacktestAmbiguousBarPolicy.StopFirst,
            BacktestEndOfDataPolicy.CloseLastObserved), new Dictionary<string, decimal>
        {
            ["fastEmaPeriod"] = 20, ["slowEmaPeriod"] = 50, ["atrPeriod"] = 14,
            ["adxPeriod"] = 14, ["volumeAveragePeriod"] = 20, ["breakoutLookbackBars"] = 3,
            ["minimumAdx"] = 20, ["volumeMultiplier"] = 1.2m, ["atrStopMultiple"] = 1,
            ["rewardRiskMultiple"] = 3, ["entryWindowStartMinuteOfDay"] = 570,
            ["entryWindowEndMinuteOfDay"] = 690
        }));

    private sealed class FixtureRunner(bool tamperHash = false, bool tamperTrade = false) : ILeanProcessRunner
    {
        public int ObservedCandles { get; private set; }

        public async Task<LeanProcessResult> RunAsync(LeanMappedInput input, CancellationToken cancellationToken)
        {
            var request = JsonDocument.Parse(await File.ReadAllTextAsync(input.RequestPath, cancellationToken));
            ObservedCandles = request.RootElement.GetProperty("candleCount").GetInt32();
            var official = Path.Combine(input.OutputDirectory, "12345.json");
            var officialJson = tamperTrade
                ? "{\"TotalPerformance\":{\"ClosedTrades\":[{\"EntryTime\":\"2026-09-01T03:50:00Z\",\"ExitTime\":\"2026-09-01T03:55:00Z\",\"EntryPrice\":101,\"ExitPrice\":103,\"Quantity\":1,\"ProfitLoss\":2,\"TotalFees\":0}]}}"
                : "{\"TotalPerformance\":{\"ClosedTrades\":[]}}";
            await File.WriteAllTextAsync(official, officialJson, cancellationToken);
            var specification = request.RootElement.GetProperty("specification");
            var declared = specification.GetProperty("data").GetProperty("datasetSha256").GetString()!;
            var trades = tamperTrade ? new[]
            {
                new BacktestRunTrade("vwap-ema-trend-breakout-v1", InstrumentId,
                    BacktestRunTradeDirection.Long, From, From.AddMinutes(5),
                    From.AddMinutes(10), 1, 101, 99, 107, 102,
                    "session-exit", 1, 0, 1, 100_001)
            } : [];
            var image = request.RootElement.GetProperty("leanImage").GetString()!;
            var run = BacktestRunCodec.Seal(new(1, LeanBacktestEngine.Id, $"2:{image}",
                BacktestEngineRole.IndependentValidation,
                tamperHash ? new string('b', 64) :
                    request.RootElement.GetProperty("specificationSha256").GetString()!,
                declared, input.ConsumedMarketDataSha256, 100_000,
                tamperTrade ? 100_001 : 100_000, tamperTrade ? 1 : 0,
                tamperTrade ? 1 : 0, 0, trades, [], string.Empty));
            await File.WriteAllTextAsync(input.EvidencePath, BacktestRunCodec.Serialize(run),
                cancellationToken);
            return new(official, input.EvidencePath);
        }
    }

    private static LeanOptions Options(string dataDirectory)
    {
        var project = Path.Combine(dataDirectory, "lean-project"); Directory.CreateDirectory(project);
        const string source = "# fixture independent LEAN source\n";
        File.WriteAllText(Path.Combine(project, "main.py"), source);
        var revision = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        File.WriteAllText(Path.Combine(project, "tcc-strategy-manifest.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, strategyId = "vwap-ema-trend-breakout-v1",
            strategyImplementationVersion = "fixture-v1", algorithmSourceRevision = revision
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return new() { DataDirectory = dataDirectory, ProjectDirectory = project,
            Image = $"quantconnect/lean@sha256:{new string('a', 64)}" };
    }

    private sealed class Store : IMarketDataStore
    {
        public Task AddInstrumentAsync(Instrument instrument, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task AddCandlesAsync(IReadOnlyCollection<Candle> candles,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Instrument?> FindInstrumentAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Instrument?>(id == InstrumentId ?
                new Instrument(InstrumentId, "NSE", "NIFTY 50", "Fixture", 1, .05m) : null);
        public Task<IReadOnlyList<Candle>> ReadCandlesAsync(Guid instrumentId, Timeframe timeframe,
            DateTimeOffset from, DateTimeOffset to, int limit = 10000,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Candle>>(
            [
                new(InstrumentId, Timeframe.Minute5, From, 100, 101, 99, 100, 100),
                new(InstrumentId, Timeframe.Minute5, From.AddMinutes(5), 101, 102, 100, 101, 110)
            ]);
    }
}
