using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trading.Application.Backtesting;
using Trading.Application.MarketData;
using Trading.Backtesting.Comparison;
using Trading.Backtesting.Engines;
using Trading.Domain.MarketData;
using Trading.ExternalValidation.Lean;

namespace Trading.BacktestTests;

public sealed class LeanIndependentStrategyParityTests
{
    private static readonly Guid InstrumentId = Guid.Parse("38900000-0000-0000-0000-000000000001");
    private static readonly TimeZoneInfo India = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");

    [Fact]
    public async Task Independent_vwap_ema_implementation_passes_strict_M30_fixture()
    {
        var candles = Candles();
        var specification = Specification(candles);
        var native = await new NativeBacktestEngine(new Store(candles)).RunAsync(specification);
        Assert.NotEmpty(native.Trades);
        var root = RepositoryRoot();
        var project = Path.Combine(root, "external", "lean", "TradingCommandCenterLean");
        var evidence = LeanAlgorithmProject.Verify(project, specification.Specification.StrategyId);
        var directory = Path.Combine(Path.GetTempPath(), $"lean-parity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var mapped = await LeanInputMapper.WriteAsync(directory, specification, candles, evidence,
                $"quantconnect/lean@sha256:{new string('a', 64)}", CancellationToken.None);
            var output = Path.Combine(directory, "lean-run.json");
            using var process = new Process { StartInfo = new("python")
            {
                WorkingDirectory = project, UseShellExecute = false, RedirectStandardError = true,
                RedirectStandardOutput = true, CreateNoWindow = true
            }};
            process.StartInfo.ArgumentList.Add("parity_harness.py");
            process.StartInfo.ArgumentList.Add("--request"); process.StartInfo.ArgumentList.Add(mapped.RequestPath);
            process.StartInfo.ArgumentList.Add("--candles"); process.StartInfo.ArgumentList.Add(mapped.CandlePath);
            process.StartInfo.ArgumentList.Add("--output"); process.StartInfo.ArgumentList.Add(output);
            Assert.True(process.Start()); await process.WaitForExitAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            Assert.True(process.ExitCode == 0, stderr);
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false));
            var portable = JsonSerializer.Deserialize<BacktestRun>(await File.ReadAllTextAsync(output), options)!;
            var lean = BacktestRunCodec.Seal(portable);

            var comparison = CrossEngineTradeComparer.Compare(native, lean);

            Assert.Equal(CrossEngineVerdict.Pass, comparison.Verdict);
            Assert.Equal(1m, comparison.MatchedTradeRate);
            Assert.Equal(1m, comparison.EntryTimestampMatchRate);
            Assert.Equal(1m, comparison.ExitTimestampMatchRate);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static SealedBacktestSpecification Specification(IReadOnlyList<Candle> candles) =>
        BacktestSpecificationCodec.Seal(new(1, "vwap-ema-trend-breakout-v1",
            new(InstrumentId, "NSE", "LEANFIXTURE", BacktestAssetClass.EquityIndex, "INR", 1, .05m),
            new(candles[0].OpenTimeUtc, candles[^1].OpenTimeUtc.AddMinutes(5), 5,
                "India Standard Time", "nse-fixture", "fixture", "v1", new string('a', 64),
                BacktestMarketDataMode.OhlcvBars),
            new(100_000, 750, 30_000, 5),
            new(3, 0, "none", new TimeOnly(15, 25), BacktestSignalTiming.CompletedBar,
                BacktestEntryFillPolicy.NextObservedBarOpen, BacktestAmbiguousBarPolicy.StopFirst,
                BacktestEndOfDataPolicy.CloseLastObserved),
            new Dictionary<string, decimal> { ["fastEmaPeriod"] = 3, ["slowEmaPeriod"] = 6,
                ["atrPeriod"] = 3, ["adxPeriod"] = 3, ["volumeAveragePeriod"] = 3,
                ["breakoutLookbackBars"] = 3, ["minimumAdx"] = 5, ["volumeMultiplier"] = 1.1m,
                ["atrStopMultiple"] = 1, ["rewardRiskMultiple"] = 3,
                ["entryWindowStartMinuteOfDay"] = 555, ["entryWindowEndMinuteOfDay"] = 930 }));

    private static Candle[] Candles()
    {
        var values = new List<Candle>(); decimal previous = 100;
        for (var day = 0; day < 2; day++)
        {
            var date = new DateOnly(2025, 1, 6 + day);
            for (var index = 0; index < 76; index++)
            {
                var close = previous + (day == 0 ? .4m : -.5m);
                var local = date.ToDateTime(new TimeOnly(9, 15).AddMinutes(index * 5), DateTimeKind.Unspecified);
                values.Add(new(InstrumentId, Timeframe.Minute5, TimeZoneInfo.ConvertTimeToUtc(local, India),
                    previous, decimal.Max(previous, close) + .2m, decimal.Min(previous, close) - .2m,
                    close, index == 20 ? 300 : 100));
                previous = close;
            }
            if (day == 0) previous += 3;
        }
        return values.ToArray();
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null;
             directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "TradingCommandCenter.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException();
    }

    private sealed class Store(IReadOnlyList<Candle> candles) : IMarketDataStore
    {
        public Task AddInstrumentAsync(Instrument instrument, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task AddCandlesAsync(IReadOnlyCollection<Candle> values, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<Instrument?> FindInstrumentAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Instrument?>(new(InstrumentId, "NSE", "LEANFIXTURE", "Fixture", 1, .05m));
        public Task<IReadOnlyList<Candle>> ReadCandlesAsync(Guid instrumentId, Timeframe timeframe,
            DateTimeOffset from, DateTimeOffset to, int limit = 10000,
            CancellationToken cancellationToken = default) => Task.FromResult(candles);
    }
}
