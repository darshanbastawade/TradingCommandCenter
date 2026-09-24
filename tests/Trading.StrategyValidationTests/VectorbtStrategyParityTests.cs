using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Trading.Domain.MarketData;
using Trading.Strategies.AdxTrendContinuation;
using Trading.Strategies.Contracts;
using Trading.Strategies.EmaPullbackContinuation;
using Trading.Strategies.Indicators;
using Trading.Strategies.OpeningRangeBreakout;
using Trading.Strategies.VwapEmaTrendBreakout;
using Trading.Strategies.VwapReclaimRejection;

namespace Trading.StrategyValidationTests;

public sealed class VectorbtStrategyParityTests
{
    private static readonly TimeZoneInfo India = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [Theory]
    [InlineData(VwapEmaTrendBreakoutStrategy.StrategyId)]
    [InlineData(OpeningRangeBreakoutStrategy.StrategyId)]
    [InlineData(EmaPullbackContinuationStrategy.StrategyId)]
    [InlineData(VwapReclaimRejectionStrategy.StrategyId)]
    [InlineData(AdxTrendContinuationStrategy.StrategyId)]
    public async Task Python_port_has_exact_native_signal_parity_and_indicator_parity(string strategyId)
    {
        var candles = Candles(strategyId);
        var parameters = Parameters(strategyId);
        var strategy = Strategy(strategyId);
        var expectedSignals = strategy.Evaluate(candles, India)
            .Select(x => new ParitySignal(x.OpenTimeUtc, x.Direction == TradeDirection.Long ? "long" : "short"))
            .ToArray();
        Assert.Contains(expectedSignals, x => x.Direction == "long");
        Assert.Contains(expectedSignals, x => x.Direction == "short");
        if (strategyId == VwapEmaTrendBreakoutStrategy.StrategyId)
        {
            var boundary = candles.Where(x => TimeZoneInfo.ConvertTimeFromUtc(x.OpenTimeUtc, India).TimeOfDay ==
                new TimeSpan(15, 30, 0)).Select(x => x.OpenTimeUtc).ToHashSet();
            Assert.DoesNotContain(expectedSignals, x => boundary.Contains(x.TimestampUtc));
            var boundaryAllowed = new VwapEmaTrendBreakoutStrategy(new()
            { FastEmaPeriod = 3, SlowEmaPeriod = 6, AtrPeriod = 3, AdxPeriod = 3,
              VolumeAveragePeriod = 3, BreakoutLookbackBars = 3, MinimumAdx = 5,
              VolumeMultiplier = 1.1m, EntryWindowStart = new(9, 15), EntryWindowEnd = new(15, 31) });
            Assert.Contains(boundaryAllowed.Evaluate(candles, India), x => boundary.Contains(x.OpenTimeUtc));
        }
        var expectedIndicators = IndicatorEngine.Calculate(candles, India, new(3, 6, 3, 3, 3));
        var fixture = new
        {
            schemaVersion = 1,
            strategyId,
            exchangeTimeZoneId = "India Standard Time",
            parameters,
            candles = candles.Select(x => new { x.OpenTimeUtc, x.Open, x.High, x.Low, x.Close, x.Volume }),
            expectedSignals,
            expectedIndicators
        };

        var root = RepositoryRoot();
        var directory = Path.Combine(Path.GetTempPath(), $"tcc-parity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "fixture.json");
        var output = Path.Combine(directory, "actual.json");
        try
        {
            await File.WriteAllTextAsync(input, JsonSerializer.Serialize(fixture, Json));
            using var process = new Process
            {
                StartInfo = new("python")
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.Combine(root, "workers", "vectorbt")
                }
            };
            process.StartInfo.ArgumentList.Add("parity.py");
            process.StartInfo.ArgumentList.Add("--fixture"); process.StartInfo.ArgumentList.Add(input);
            process.StartInfo.ArgumentList.Add("--output"); process.StartInfo.ArgumentList.Add(output);
            Assert.True(process.Start());
            await process.WaitForExitAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            Assert.True(process.ExitCode == 0, stderr);

            using var actual = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            var actualSignals = actual.RootElement.GetProperty("signals").EnumerateArray()
                .Select(x => new ParitySignal(DateTime.Parse(x.GetProperty("timestampUtc").GetString()!,
                    CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                    x.GetProperty("direction").GetString()!)).ToArray();
            Assert.Equal(expectedSignals, actualSignals);
            Assert.Equal("native-signals-v1", actual.RootElement.GetProperty("strategyPortVersion").GetString());
            Assert.Equal(64, actual.RootElement.GetProperty("strategyPortSha256").GetString()!.Length);

            var actualIndicators = actual.RootElement.GetProperty("indicators").EnumerateArray().ToArray();
            Assert.Equal(expectedIndicators.Count, actualIndicators.Length);
            for (var i = 0; i < expectedIndicators.Count; i++)
            {
                Equal(expectedIndicators[i].FastEma, actualIndicators[i], "fastEma");
                Equal(expectedIndicators[i].SlowEma, actualIndicators[i], "slowEma");
                Equal(expectedIndicators[i].Atr, actualIndicators[i], "atr");
                Equal(expectedIndicators[i].Adx, actualIndicators[i], "adx");
                Equal(expectedIndicators[i].PositiveDi, actualIndicators[i], "positiveDi");
                Equal(expectedIndicators[i].NegativeDi, actualIndicators[i], "negativeDi");
                Equal(expectedIndicators[i].AverageVolume, actualIndicators[i], "averageVolume");
                Equal(expectedIndicators[i].SessionVwap, actualIndicators[i], "sessionVwap");
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void Equal(decimal? expected, JsonElement item, string name)
    {
        var value = item.GetProperty(name);
        if (expected is null) { Assert.Equal(JsonValueKind.Null, value.ValueKind); return; }
        var actual = decimal.Parse(value.GetString()!, CultureInfo.InvariantCulture);
        Assert.True(decimal.Abs(expected.Value - actual) <= 0.00000000000000000001m,
            $"{name} differs: native={expected} python={actual}");
    }

    private static IReadOnlyDictionary<string, decimal> Parameters(string strategyId)
    {
        var values = new Dictionary<string, decimal>
        {
            ["fastEmaPeriod"] = 3, ["slowEmaPeriod"] = 6, ["atrPeriod"] = 3,
            ["adxPeriod"] = 3, ["volumeAveragePeriod"] = 3, ["minimumAdx"] = 5,
            ["volumeMultiplier"] = 1.1m, ["atrStopMultiple"] = 1,
            ["rewardRiskMultiple"] = 3, ["entryWindowStartMinuteOfDay"] = 555,
            ["entryWindowEndMinuteOfDay"] = 930
        };
        if (strategyId == VwapEmaTrendBreakoutStrategy.StrategyId) values["breakoutLookbackBars"] = 3;
        if (strategyId == OpeningRangeBreakoutStrategy.StrategyId) values["openingRangeBars"] = 3;
        if (strategyId == AdxTrendContinuationStrategy.StrategyId) values.Remove("volumeMultiplier");
        return values;
    }

    private static ITradingStrategy Strategy(string id) => id switch
    {
        VwapEmaTrendBreakoutStrategy.StrategyId => new VwapEmaTrendBreakoutStrategy(new()
        { FastEmaPeriod = 3, SlowEmaPeriod = 6, AtrPeriod = 3, AdxPeriod = 3,
          VolumeAveragePeriod = 3, BreakoutLookbackBars = 3, MinimumAdx = 5,
          VolumeMultiplier = 1.1m, EntryWindowStart = new(9, 15), EntryWindowEnd = new(15, 30) }),
        OpeningRangeBreakoutStrategy.StrategyId => new OpeningRangeBreakoutStrategy(new()
        { FastEmaPeriod = 3, SlowEmaPeriod = 6, AtrPeriod = 3, AdxPeriod = 3,
          VolumeAveragePeriod = 3, OpeningRangeBars = 3, VolumeMultiplier = 1.1m,
          EntryWindowStart = new(9, 15), EntryWindowEnd = new(15, 30) }),
        EmaPullbackContinuationStrategy.StrategyId => new EmaPullbackContinuationStrategy(new()
        { FastEmaPeriod = 3, SlowEmaPeriod = 6, AtrPeriod = 3, AdxPeriod = 3,
          VolumeAveragePeriod = 3, MinimumAdx = 5, VolumeMultiplier = 1.1m,
          EntryWindowStart = new(9, 15), EntryWindowEnd = new(15, 30) }),
        VwapReclaimRejectionStrategy.StrategyId => new VwapReclaimRejectionStrategy(new()
        { FastEmaPeriod = 3, SlowEmaPeriod = 6, AtrPeriod = 3, AdxPeriod = 3,
          VolumeAveragePeriod = 3, MinimumAdx = 5, VolumeMultiplier = 1.1m,
          EntryWindowStart = new(9, 15), EntryWindowEnd = new(15, 30) }),
        AdxTrendContinuationStrategy.StrategyId => new AdxTrendContinuationStrategy(new()
        { FastEmaPeriod = 3, SlowEmaPeriod = 6, AtrPeriod = 3, AdxPeriod = 3,
          VolumeAveragePeriod = 3, MinimumAdx = 5,
          EntryWindowStart = new(9, 15), EntryWindowEnd = new(15, 30) }),
        _ => throw new ArgumentOutOfRangeException(nameof(id))
    };

    private static Candle[] Candles(string strategyId)
    {
        var values = new List<Candle>();
        var instrument = Guid.Parse("38383838-3838-3838-3838-383838383838");
        decimal previous = 100;
        for (var day = 0; day < 2; day++)
        {
            var date = new DateOnly(2025, 1, 6 + day);
            for (var index = 0; index < 76; index++)
            {
                decimal close;
                if (strategyId == VwapEmaTrendBreakoutStrategy.StrategyId)
                    close = previous + (day == 0 ? .4m : -.5m);
                else if (strategyId == EmaPullbackContinuationStrategy.StrategyId)
                    close = day == 0
                        ? index == 30 ? previous - 2m : index == 31 ? previous + 3m : previous + .4m
                        : index == 30 ? previous + 2.5m : index == 31 ? previous - 3.5m : previous - .5m;
                else if (day == 0)
                    close = index == 30 ? previous - 8 : index == 31 ? previous + 9 : previous + .4m;
                else
                    close = index == 30 ? previous + 12 : index == 31 ? previous - 13 : previous - .5m;
                var open = previous;
                var high = decimal.Max(open, close) + .2m;
                var low = decimal.Min(open, close) - .2m;
                var signalIndex = strategyId == VwapEmaTrendBreakoutStrategy.StrategyId ? 20 : 31;
                var volume = index == signalIndex ? 300L : index == 75 ? 500L : 100L;
                var local = date.ToDateTime(new TimeOnly(9, 15).AddMinutes(index * 5), DateTimeKind.Unspecified);
                var utc = TimeZoneInfo.ConvertTimeToUtc(local, India);
                values.Add(new(instrument, Timeframe.Minute5, utc, open, high, low, close, volume));
                previous = close;
            }
            previous += day == 0 ? 3 : 0;
        }
        return values.ToArray();
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "TradingCommandCenter.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed record ParitySignal(DateTime TimestampUtc, string Direction);
}
