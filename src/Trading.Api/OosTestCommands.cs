using System.Globalization;
using System.Text;
using System.Text.Json;
using Trading.Application.MarketData;
using Trading.Backtesting;
using Trading.Backtesting.Costs;
using Trading.Backtesting.Reporting;
using Trading.Backtesting.Validation;
using Trading.Domain.MarketData;
using Trading.Strategies.VwapEmaTrendBreakout;
using Trading.Strategies;
using Trading.Strategies.Contracts;

namespace Trading.Api;

public static class OosTestCommands
{
    private const string EquityProfile = "zerodha-nse-equity-intraday-2026-03-01";
    private const string OptionsProfile = "zerodha-nse-equity-options-2026-04-01";

    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] == "run-oos";

    public static async Task<int> RunAsync(string[] args, IServiceProvider services, TextWriter output,
        TextWriter error, CancellationToken cancellationToken = default)
    {
        try
        {
            var values = Parse(args);
            var instrumentId = Guid.Parse(Required(values, "instrument-id"));
            var timeframe = (Timeframe)PositiveInt(values, "timeframe");
            if (!Enum.IsDefined(timeframe)) throw new ArgumentException("Unsupported --timeframe value.");
            var from = Timestamp(values, "from");
            var to = Timestamp(values, "to");
            if (from >= to) throw new ArgumentException("--from must precede --to.");
            var trainingSessions = PositiveInt(values, "training-sessions");
            var embargoSessions = NonNegativeInt(values, "embargo-sessions", 0);
            var initialCapital = PositiveDecimal(values, "initial-capital");
            var allowedRisk = PositiveDecimal(values, "allowed-risk");
            var maximumCapital = PositiveDecimal(values, "maximum-capital");
            int? maximumLots = values.ContainsKey("maximum-lots") ? PositiveInt(values, "maximum-lots") : null;
            var slippageBps = NonNegativeDecimal(values, "slippage-bps");
            if (slippageBps >= 10_000m) throw new ArgumentException("--slippage-bps must be below 10,000.");
            var costProfile = Required(values, "cost-profile");
            var strategyId = values.GetValueOrDefault("strategy-id", VwapEmaTrendBreakoutStrategy.StrategyId);
            _ = CreateStrategy(strategyId);
            var destination = Path.GetFullPath(Required(values, "output"));
            if (!string.Equals(Path.GetExtension(destination), ".json", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("--output must use the .json extension.");
            if (!Directory.Exists(Path.GetDirectoryName(destination)))
                throw new ArgumentException("The --output directory does not exist.");

            await using var scope = services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IMarketDataStore>();
            var instrument = await store.FindInstrumentAsync(instrumentId, cancellationToken) ??
                throw new ArgumentException("The requested instrument is not registered.");
            var candles = await ReadAllAsync(store, instrumentId, timeframe, from, to, cancellationToken);
            if (candles.Count == 0) throw new ArgumentException("No candles exist in the requested range.");

            var zone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            var backtestSettings = new BacktestSettings
            {
                InitialCapital = initialCapital,
                SlippageBasisPointsPerSide = slippageBps,
                CostModel = CostModel(costProfile),
                RiskBasedSizing = new(allowedRisk, instrument.LotSize, maximumCapital, maximumLots)
            };
            var fold = TimeSeriesValidationEngine.RunHoldout(
                (_, _) => CreateStrategy(strategyId), candles, zone,
                new(trainingSessions, embargoSessions), backtestSettings);
            var artifact = OosTestArtifactBuilder.Build(fold, candles, zone);
            var json = BacktestReportJson.Serialize(artifact);
            await WriteNewAtomicallyAsync(destination, json, cancellationToken);

            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                status = "oos-report-created",
                output = destination,
                strategyId = artifact.SelectedStrategyId,
                trainingSessions = artifact.Window.TrainingSessionCount,
                testingSessions = artifact.Window.TestingSessionCount,
                trades = artifact.Report.Summary.TotalTrades,
                netPnl = artifact.Report.Summary.NetPnl
            }));
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("OOS test cancelled; no completed report should be assumed.");
            return 130;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            await error.WriteLineAsync(exception.Message);
            return 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync("Unable to create the OOS report. The output must be a new writable .json file.");
            return 2;
        }
        catch (Exception)
        {
            await error.WriteLineAsync("OOS testing failed. Check database readiness, the requested range and report settings.");
            return 3;
        }
    }

    private static async Task WriteNewAtomicallyAsync(string destination, string contents,
        CancellationToken cancellationToken)
    {
        if (File.Exists(destination)) throw new IOException("The output file already exists.");
        var temporary = $"{destination}.tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                await writer.WriteAsync(contents.AsMemory(), cancellationToken);
            File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task<IReadOnlyList<Candle>> ReadAllAsync(IMarketDataStore store, Guid instrumentId,
        Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        const int pageSize = 10_000;
        var candles = new List<Candle>();
        var cursor = from;
        while (cursor < to)
        {
            var page = await store.ReadCandlesAsync(instrumentId, timeframe, cursor, to, pageSize, cancellationToken);
            candles.AddRange(page);
            if (page.Count < pageSize) break;
            cursor = new DateTimeOffset(page[^1].OpenTimeUtc, TimeSpan.Zero).AddTicks(1);
        }
        return candles.AsReadOnly();
    }

    private static ITradeCostModel? CostModel(string id) => id switch
    {
        "none" => null,
        EquityProfile => IndianCostProfiles.ZerodhaNseEquityIntraday2026(),
        OptionsProfile => IndianCostProfiles.ZerodhaNseEquityOptions2026(),
        _ => throw new ArgumentException($"--cost-profile must be none, {EquityProfile}, or {OptionsProfile}.")
    };

    private static Dictionary<string, string> Parse(string[] args)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "instrument-id", "timeframe", "from", "to", "training-sessions", "embargo-sessions",
            "initial-capital", "allowed-risk", "maximum-capital", "maximum-lots", "slippage-bps",
            "cost-profile", "strategy-id", "output"
        };
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Expected a named --option.");
            var key = args[index][2..];
            if (!allowed.Contains(key)) throw new ArgumentException($"Unknown option: --{key}");
            if (++index == args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Missing value for --{key}");
            if (!result.TryAdd(key, args[index])) throw new ArgumentException($"Duplicate option: --{key}");
        }
        return result;
    }

    private static string Required(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException($"Missing --{key}");

    private static int PositiveInt(IReadOnlyDictionary<string, string> values, string key)
    {
        var value = int.Parse(Required(values, key), NumberStyles.None, CultureInfo.InvariantCulture);
        return value > 0 ? value : throw new ArgumentException($"--{key} must be positive.");
    }

    private static int NonNegativeInt(IReadOnlyDictionary<string, string> values, string key, int fallback)
    {
        if (!values.ContainsKey(key)) return fallback;
        var value = int.Parse(Required(values, key), NumberStyles.None, CultureInfo.InvariantCulture);
        return value >= 0 ? value : throw new ArgumentException($"--{key} cannot be negative.");
    }

    private static decimal PositiveDecimal(IReadOnlyDictionary<string, string> values, string key)
    {
        var value = decimal.Parse(Required(values, key), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        return value > 0 ? value : throw new ArgumentException($"--{key} must be positive.");
    }

    private static decimal NonNegativeDecimal(IReadOnlyDictionary<string, string> values, string key)
    {
        var value = decimal.Parse(Required(values, key), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        return value >= 0 ? value : throw new ArgumentException($"--{key} cannot be negative.");
    }

    private static DateTimeOffset Timestamp(IReadOnlyDictionary<string, string> values, string key)
    {
        var text = Required(values, key);
        var suffix = text.Length >= 6 ? text[^6..] : string.Empty;
        if (!text.EndsWith('Z') && !suffix.Contains('+') && !suffix.Contains('-'))
            throw new ArgumentException($"--{key} must include Z or an explicit UTC offset.");
        return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static ITradingStrategy CreateStrategy(string id) => StrategyCatalog.CreateDefaults()
        .SingleOrDefault(strategy => strategy.Id == id) ??
        throw new ArgumentException($"Unknown --strategy-id: {id}");
}
