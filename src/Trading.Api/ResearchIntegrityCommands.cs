using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Trading.Application.MarketData;
using Trading.Application.Research;
using Trading.Backtesting;
using Trading.Backtesting.Costs;
using Trading.Backtesting.Ranking;
using Trading.Backtesting.Reporting;
using Trading.Backtesting.Robustness;
using Trading.Backtesting.Validation;
using Trading.Domain.MarketData;
using Trading.Domain.Research;
using Trading.MarketData.Quality;
using Trading.Strategies.AdxTrendContinuation;
using Trading.Strategies.Contracts;
using Trading.Strategies.EmaPullbackContinuation;
using Trading.Strategies.OpeningRangeBreakout;
using Trading.Strategies.Regimes;
using Trading.Strategies.VwapEmaTrendBreakout;
using Trading.Strategies.VwapReclaimRejection;

namespace Trading.Api;

public static class ResearchIntegrityCommands
{
    private const string EquityProfile = "zerodha-nse-equity-intraday-2026-03-01";
    private const string OptionsProfile = "zerodha-nse-equity-options-2026-04-01";

    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] == "run-research";

    public static async Task<int> RunAsync(string[] args, IServiceProvider services, TextWriter output,
        TextWriter error, CancellationToken cancellationToken = default)
    {
        string? createdFile = null;
        try
        {
            var values = Parse(args);
            var instrumentId = Guid.Parse(Required(values, "instrument-id"));
            var timeframe = (Timeframe)PositiveInt(values, "timeframe");
            var fromSession = Date(values, "from-session");
            var toSession = Date(values, "to-session-exclusive");
            if (fromSession >= toSession) throw new ArgumentException("--from-session must precede --to-session-exclusive.");
            var trainingSessions = PositiveInt(values, "training-sessions");
            var testingSessions = PositiveInt(values, "testing-sessions");
            var embargoSessions = NonNegativeInt(values, "embargo-sessions", 0);
            var mode = Required(values, "training-mode");
            var anchored = mode switch { "rolling" => false, "anchored" => true,
                _ => throw new ArgumentException("--training-mode must be rolling or anchored.") };
            var initialCapital = PositiveDecimal(values, "initial-capital");
            var allowedRisk = PositiveDecimal(values, "allowed-risk");
            var maximumCapital = PositiveDecimal(values, "maximum-capital");
            int? maximumLots = values.ContainsKey("maximum-lots") ? PositiveInt(values, "maximum-lots") : null;
            var slippage = NonNegativeDecimal(values, "slippage-bps");
            if (slippage + 5m >= 10_000m)
                throw new ArgumentException("--slippage-bps must leave room for the 5 bps adverse stress scenario.");
            var costProfile = Required(values, "cost-profile");
            _ = CostModel(costProfile);
            var sourceRevision = Required(values, "source-revision");
            var dataSource = Required(values, "data-source");
            var dataVersion = Required(values, "data-version");
            var calendarId = Required(values, "calendar-id");
            var calendarFile = Path.GetFullPath(CalendarFile(values));
            var destination = Path.GetFullPath(Required(values, "output"));
            ValidateOutput(destination);

            var zone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            var entries = DatasetQualityCertifier.ParseCalendar(
                await File.ReadAllLinesAsync(calendarFile, cancellationToken));
            var calendar = new ExchangeSessionCalendar(calendarId, zone, new(9, 15), new(15, 30),
                entries.Holidays, entries.SpecialSessions);
            var from = AtLocalMidnight(fromSession, calendar);
            var to = AtLocalMidnight(toSession, calendar);

            await using var scope = services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IMarketDataStore>();
            var instrument = await store.FindInstrumentAsync(instrumentId, cancellationToken) ??
                throw new ArgumentException("The requested instrument is not registered.");
            var rawCandles = await ReadAllAsync(store, instrumentId, timeframe, from, to, cancellationToken);
            var certification = DatasetQualityCertifier.Certify(rawCandles, instrumentId, timeframe, fromSession,
                toSession, calendar, dataSource, dataVersion);
            var certificate = certification.Certificate;
            if (!certificate.Passed)
            {
                await error.WriteLineAsync(JsonSerializer.Serialize(certificate));
                return 2;
            }
            var candles = certification.CertifiedCandles;

            var configuration = services.GetRequiredService<IConfiguration>();
            var rankingSettings = configuration.GetSection("StrategyRanking").Get<StrategyRankingSettings>() ?? new();
            var runConfiguration = new ResearchRunConfiguration(trainingSessions, testingSessions,
                embargoSessions, mode, initialCapital, allowedRisk, maximumCapital, maximumLots,
                slippage, costProfile, rankingSettings);
            var configurationJson = JsonSerializer.Serialize(runConfiguration);
            var configurationHash = Sha256(configurationJson);
            var definitions = Definitions();
            var evidence = new List<StrategyResearchEvidence>();
            var evaluations = new List<StrategyEvaluation>();
            var regimes = MarketRegimeClassifier.Classify(candles, zone);

            foreach (var definition in definitions)
            {
                var settings = Settings(instrument.LotSize, initialCapital, allowedRisk, maximumCapital,
                    maximumLots, slippage, costProfile, 0);
                var holdout = TimeSeriesValidationEngine.RunHoldout((_, _) => definition.Baseline(), candles,
                    zone, new(trainingSessions, embargoSessions), settings);
                var holdoutArtifact = OosTestArtifactBuilder.Build(holdout, candles, zone);
                var testingCandles = InRange(candles, holdout.Window.TestingStartSession,
                    holdout.Window.TestingEndSession, zone);
                var walkForward = TimeSeriesValidationEngine.RunWalkForward((_, _) => definition.Baseline(),
                    candles, zone, new(trainingSessions, testingSessions, embargoSessions, anchored), settings);
                var walkArtifact = WalkForwardTestArtifactBuilder.Build(walkForward, candles, zone, anchored);
                var robustness = ParameterRobustnessAnalyzer.Analyze(definition.Neighborhood,
                    factory => TimeSeriesValidationEngine.RunHoldout((_, _) => factory(), candles, zone,
                        new(trainingSessions, embargoSessions), settings).OutOfSampleBacktest,
                    testingCandles, zone);
                var regimeRows = RegimePerformanceAnalyzer.Analyze(holdout.OutOfSampleBacktest, regimes);
                var bestTradeRemoval = ResearchStressAnalyzer.RemoveBestTrades(holdout.OutOfSampleBacktest,
                    testingCandles, zone);
                var stressScenarios = new[]
                {
                    new ExecutionStressScenario("baseline", slippage, 0, true),
                    new ExecutionStressScenario("slippage-plus-5bps", slippage + 5m, 0),
                    new ExecutionStressScenario("cost-plus-5-per-side", slippage, 5m),
                    new ExecutionStressScenario("combined-adverse", slippage + 5m, 5m)
                };
                var executionStress = ResearchStressAnalyzer.ExecutionSensitivity(stressScenarios,
                    scenario => TimeSeriesValidationEngine.RunHoldout((_, _) => definition.Baseline(), candles,
                        zone, new(trainingSessions, embargoSessions), Settings(instrument.LotSize, initialCapital,
                            allowedRisk, maximumCapital, maximumLots, scenario.SlippageBasisPointsPerSide,
                            costProfile, scenario.AdditionalFixedCostPerSide)).OutOfSampleBacktest,
                    testingCandles, zone);
                var profitableRegimes = regimeRows.Where(row => row.Dimension == "Trend" && row.Regime != "Unknown").ToArray();
                var profitableRegimeRate = profitableRegimes.Length == 0 ? 0
                    : (decimal)profitableRegimes.Count(row => row.NetPnl > 0) / profitableRegimes.Length;
                evidence.Add(new(definition.Id, holdoutArtifact, walkArtifact, robustness, regimeRows,
                    bestTradeRemoval, executionStress));
                evaluations.Add(new(definition.Id, holdout.OutOfSampleMetrics,
                    walkForward.ProfitableFoldRate, robustness.StabilityScore, profitableRegimeRate));
            }

            var ranking = StrategyRankingEngine.Rank(evaluations, rankingSettings);
            var runId = Guid.NewGuid();
            var createdAt = DateTimeOffset.UtcNow;
            var artifact = new ResearchIntegrityArtifact(1, runId, createdAt.UtcDateTime, sourceRevision,
                certificate, configurationHash, runConfiguration, evidence.AsReadOnly(), ranking);
            var artifactJson = BacktestReportJson.Serialize(artifact);
            var artifactHash = Sha256(artifactJson);
            await WriteNewAtomicallyAsync(destination, artifactJson, cancellationToken);
            createdFile = destination;
            var run = new ResearchRun(runId, createdAt, instrumentId, timeframe, from, to, dataSource,
                dataVersion, calendarId, certificate.DatasetSha256, configurationHash, artifactHash,
                sourceRevision, artifactJson);
            await scope.ServiceProvider.GetRequiredService<IResearchRunStore>().AddAsync(run, cancellationToken);
            await output.WriteLineAsync(JsonSerializer.Serialize(new { status = "research-run-created",
                runId, output = destination, datasetSha256 = certificate.DatasetSha256,
                artifactSha256 = artifactHash, rawCandles = certificate.RawCandleCount,
                certifiedCandles = certificate.CertifiedCandleCount,
                excludedCandles = certificate.ExcludedCandleCount, strategies = evidence.Count,
                qualified = ranking.SelectedStrategies.Select(item => item.StrategyId) }));
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (createdFile is not null && File.Exists(createdFile)) File.Delete(createdFile);
            await error.WriteLineAsync("Research run cancelled; no completed run should be assumed.");
            return 130;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException or IOException or UnauthorizedAccessException)
        {
            if (createdFile is not null && File.Exists(createdFile)) File.Delete(createdFile);
            await error.WriteLineAsync(exception.Message);
            return 2;
        }
        catch (Exception)
        {
            if (createdFile is not null && File.Exists(createdFile)) File.Delete(createdFile);
            await error.WriteLineAsync("Research run failed. Check database migration, certified data range and settings.");
            return 3;
        }
    }

    private sealed record Definition(string Id, Func<ITradingStrategy> Baseline,
        IReadOnlyList<ParameterScenario<Func<ITradingStrategy>>> Neighborhood);

    private static IReadOnlyList<Definition> Definitions() =>
    [
        DefinitionFor(VwapEmaTrendBreakoutStrategy.StrategyId, [20m, 25m, 30m], 25m,
            value => () => new VwapEmaTrendBreakoutStrategy(new() { MinimumAdx = value })),
        DefinitionFor(OpeningRangeBreakoutStrategy.StrategyId, [2m, 3m, 4m], 3m,
            value => () => new OpeningRangeBreakoutStrategy(new() { OpeningRangeBars = (int)value })),
        DefinitionFor(EmaPullbackContinuationStrategy.StrategyId, [15m, 20m, 25m], 20m,
            value => () => new EmaPullbackContinuationStrategy(new() { MinimumAdx = value })),
        DefinitionFor(VwapReclaimRejectionStrategy.StrategyId, [10m, 15m, 20m], 15m,
            value => () => new VwapReclaimRejectionStrategy(new() { MinimumAdx = value })),
        DefinitionFor(AdxTrendContinuationStrategy.StrategyId, [20m, 25m, 30m], 25m,
            value => () => new AdxTrendContinuationStrategy(new() { MinimumAdx = value }))
    ];

    private static Definition DefinitionFor(string id, IReadOnlyList<decimal> values, decimal baseline,
        Func<decimal, Func<ITradingStrategy>> factory)
    {
        var scenarios = values.Select(value => new ParameterScenario<Func<ITradingStrategy>>(
            $"primary={value.ToString(CultureInfo.InvariantCulture)}", factory(value),
            new Dictionary<string, decimal> { ["primary"] = value }, value == baseline)).ToArray();
        return new(id, factory(baseline), Array.AsReadOnly(scenarios));
    }

    private static BacktestSettings Settings(int lotSize, decimal capital, decimal risk, decimal maximumCapital,
        int? maximumLots, decimal slippage, string costProfile, decimal fixedCost) => new()
    {
        InitialCapital = capital, SlippageBasisPointsPerSide = slippage, FixedCostPerSide = fixedCost,
        CostModel = CostModel(costProfile), RiskBasedSizing = new(risk, lotSize, maximumCapital, maximumLots)
    };

    private static ITradeCostModel? CostModel(string id) => id switch
    {
        "none" => null, EquityProfile => IndianCostProfiles.ZerodhaNseEquityIntraday2026(),
        OptionsProfile => IndianCostProfiles.ZerodhaNseEquityOptions2026(),
        _ => throw new ArgumentException($"--cost-profile must be none, {EquityProfile}, or {OptionsProfile}.")
    };

    private static Candle[] InRange(IEnumerable<Candle> candles, DateOnly start, DateOnly end, TimeZoneInfo zone) =>
        candles.Where(candle => { var session = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(candle.OpenTimeUtc, zone)); return session >= start && session <= end; }).ToArray();

    private static DateTimeOffset AtLocalMidnight(DateOnly date, ExchangeSessionCalendar calendar) =>
        new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue,
            DateTimeKind.Unspecified), calendar.TimeZone), TimeSpan.Zero);

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void ValidateOutput(string destination)
    {
        if (!string.Equals(Path.GetExtension(destination), ".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("--output must use the .json extension.");
        if (!Directory.Exists(Path.GetDirectoryName(destination))) throw new ArgumentException("The --output directory does not exist.");
        if (File.Exists(destination)) throw new IOException("The output file already exists.");
    }

    private static async Task WriteNewAtomicallyAsync(string destination, string contents, CancellationToken token)
    {
        var temporary = $"{destination}.tmp-{Guid.NewGuid():N}";
        try { await File.WriteAllTextAsync(temporary, contents, new UTF8Encoding(false), token); File.Move(temporary, destination, false); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<IReadOnlyList<Candle>> ReadAllAsync(IMarketDataStore store, Guid instrumentId,
        Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken token)
    {
        const int size = 10_000; var result = new List<Candle>(); var cursor = from;
        while (cursor < to) { var page = await store.ReadCandlesAsync(instrumentId, timeframe, cursor, to, size, token); result.AddRange(page); if (page.Count < size) break; cursor = new(page[^1].OpenTimeUtc.AddTicks(1), TimeSpan.Zero); }
        return result.AsReadOnly();
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "instrument-id", "timeframe", "from-session",
            "to-session-exclusive", "training-sessions", "testing-sessions", "embargo-sessions", "training-mode",
            "initial-capital", "allowed-risk", "maximum-capital", "maximum-lots", "slippage-bps", "cost-profile",
            "source-revision", "data-source", "data-version", "calendar-id", "calendar-file",
            "holiday-file", "output" };
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index++) { if (!args[index].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Expected a named --option."); var key = args[index][2..]; if (!allowed.Contains(key)) throw new ArgumentException($"Unknown option: --{key}"); if (++index == args.Length || args[index].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Missing value for --{key}"); if (!result.TryAdd(key, args[index])) throw new ArgumentException($"Duplicate option: --{key}"); }
        return result;
    }

    private static string Required(IReadOnlyDictionary<string, string> values, string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"Missing --{key}");
    private static string CalendarFile(IReadOnlyDictionary<string, string> values)
    {
        var hasCalendar = values.TryGetValue("calendar-file", out var calendar);
        var hasHoliday = values.TryGetValue("holiday-file", out var holiday);
        if (hasCalendar == hasHoliday)
            throw new ArgumentException("Specify exactly one of --calendar-file or --holiday-file.");
        return hasCalendar ? calendar! : holiday!;
    }
    private static int PositiveInt(IReadOnlyDictionary<string, string> values, string key) { var value = int.Parse(Required(values, key), NumberStyles.None, CultureInfo.InvariantCulture); return value > 0 ? value : throw new ArgumentException($"--{key} must be positive."); }
    private static int NonNegativeInt(IReadOnlyDictionary<string, string> values, string key, int fallback) { if (!values.ContainsKey(key)) return fallback; var value = int.Parse(Required(values, key), NumberStyles.None, CultureInfo.InvariantCulture); return value >= 0 ? value : throw new ArgumentException($"--{key} cannot be negative."); }
    private static decimal PositiveDecimal(IReadOnlyDictionary<string, string> values, string key) { var value = decimal.Parse(Required(values, key), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture); return value > 0 ? value : throw new ArgumentException($"--{key} must be positive."); }
    private static decimal NonNegativeDecimal(IReadOnlyDictionary<string, string> values, string key) { var value = decimal.Parse(Required(values, key), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture); return value >= 0 ? value : throw new ArgumentException($"--{key} cannot be negative."); }
    private static DateOnly Date(IReadOnlyDictionary<string, string> values, string key) => DateOnly.ParseExact(Required(values, key), "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
