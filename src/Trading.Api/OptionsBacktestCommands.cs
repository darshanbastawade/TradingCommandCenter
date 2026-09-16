using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Trading.Application.MarketData;
using Trading.Backtesting.Costs;
using Trading.Backtesting.Options;
using Trading.Backtesting.Reporting;
using Trading.Domain.MarketData;
using Trading.Strategies;

namespace Trading.Api;

public static class OptionsBacktestCommands
{
    private const string OptionsProfile = "zerodha-nse-equity-options-2026-04-01";

    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] == "run-options-backtest";

    public static async Task<int> RunAsync(string[] args, IServiceProvider services, TextWriter output,
        TextWriter error, CancellationToken cancellationToken = default)
    {
        try
        {
            var values = Parse(args);
            var underlyingId = Guid.Parse(Required(values, "underlying-id"));
            var timeframe = (Timeframe)PositiveInt(values, "timeframe");
            if (!Enum.IsDefined(timeframe) || timeframe == Timeframe.Day1) throw new ArgumentException("Unsupported intraday --timeframe.");
            var from = Timestamp(values, "from");
            var to = Timestamp(values, "to");
            if (from >= to) throw new ArgumentException("--from must precede --to.");
            var strategyId = Required(values, "strategy-id");
            var strategy = StrategyCatalog.CreateDefaults().SingleOrDefault(item => item.Id == strategyId) ??
                throw new ArgumentException($"Unknown --strategy-id: {strategyId}");
            var costProfile = Required(values, "cost-profile");
            var costModel = costProfile switch
            {
                "none" => null,
                OptionsProfile => IndianCostProfiles.ZerodhaNseEquityOptions2026(),
                _ => throw new ArgumentException($"--cost-profile must be none or {OptionsProfile}.")
            };
            var configuration = new OptionsBacktestConfiguration(
                PositiveDecimal(values, "initial-capital"), PositiveDecimal(values, "allowed-risk"),
                PositiveDecimal(values, "maximum-capital"), values.ContainsKey("maximum-lots") ? PositiveInt(values, "maximum-lots") : null,
                Fraction(values, "premium-stop-percent"), PositiveDecimal(values, "reward-risk"),
                NonNegativeLong(values, "minimum-volume"), NonNegativeLong(values, "minimum-open-interest"),
                NonNegativeDecimal(values, "maximum-spread-bps"), NonNegativeDecimal(values, "slippage-bps"),
                costProfile, values.ContainsKey("session-exit")
                    ? TimeOnly.ParseExact(Required(values, "session-exit"), "HH:mm", CultureInfo.InvariantCulture) : new(15, 25));
            if (configuration.SlippageBasisPointsPerSide >= 10_000m)
                throw new ArgumentException("--slippage-bps must be below 10,000.");
            var destination = Path.GetFullPath(Required(values, "output"));
            if (!string.Equals(Path.GetExtension(destination), ".json", StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(Path.GetDirectoryName(destination)) || File.Exists(destination))
                throw new ArgumentException("--output must be a new .json file in an existing directory.");

            await using var scope = services.CreateAsyncScope();
            var market = scope.ServiceProvider.GetRequiredService<IMarketDataStore>();
            _ = await market.FindInstrumentAsync(underlyingId, cancellationToken) ??
                throw new ArgumentException("The underlying instrument is not registered.");
            var candles = await ReadAllCandlesAsync(market, underlyingId, timeframe, from, to, cancellationToken);
            if (candles.Count == 0) throw new ArgumentException("No underlying candles exist in the requested range.");
            var zone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            var firstSession = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, zone).DateTime);
            var lastSession = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(to, zone).DateTime);
            var optionStore = scope.ServiceProvider.GetRequiredService<IOptionMarketDataStore>();
            var contracts = await optionStore.ReadContractsAsync(underlyingId, firstSession, lastSession.AddDays(45), cancellationToken);
            if (contracts.Count == 0) throw new ArgumentException("No option contracts cover the requested range.");
            var quotes = await optionStore.ReadQuotesAsync(contracts.Select(contract => contract.Id).ToArray(), from, to, cancellationToken);
            if (quotes.Count == 0) throw new ArgumentException("No observed option quotes exist in the requested range.");
            var settings = new OptionsBacktestSettings
            {
                InitialCapital = configuration.InitialCapital,
                AllowedRiskPerTrade = configuration.AllowedRiskPerTrade,
                MaximumCapitalPerTrade = configuration.MaximumCapitalPerTrade,
                MaximumLots = configuration.MaximumLots,
                PremiumStopPercent = configuration.PremiumStopPercent,
                RewardRiskMultiple = configuration.RewardRiskMultiple,
                MinimumVolume = configuration.MinimumVolume,
                MinimumOpenInterest = configuration.MinimumOpenInterest,
                MaximumSpreadBasisPoints = configuration.MaximumSpreadBasisPoints,
                SlippageBasisPointsPerSide = configuration.SlippageBasisPointsPerSide,
                SessionExitTime = configuration.SessionExitTime,
                CostModel = costModel
            };
            var result = OptionsBacktestEngine.Run(strategy, candles, contracts, quotes, zone, settings);
            var artifact = new OptionsBacktestArtifact(1, DateTime.UtcNow, strategyId, underlyingId,
                (int)timeframe, from.UtcDateTime, to.UtcDateTime, Fingerprint(candles, contracts, quotes),
                candles.Count, contracts.Count, quotes.Count, configuration, result);
            var json = BacktestReportJson.Serialize(artifact);
            await WriteNewAsync(destination, json, cancellationToken);
            await output.WriteLineAsync(JsonSerializer.Serialize(new { status = "options-backtest-created",
                output = destination, strategyId, trades = result.Trades.Count,
                rejectedCandidates = result.RejectedCandidates.Count, netPnl = result.NetPnl,
                marketDataSha256 = artifact.MarketDataSha256 }));
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("Options backtest cancelled; no completed report should be assumed.");
            return 130;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException or IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync(exception.Message);
            return 2;
        }
        catch (Exception)
        {
            await error.WriteLineAsync("Options backtest failed. Check the M17 migration, option data coverage and settings.");
            return 3;
        }
    }

    private static async Task<IReadOnlyList<Candle>> ReadAllCandlesAsync(IMarketDataStore store, Guid id,
        Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken token)
    {
        var result = new List<Candle>();
        var cursor = from;
        while (cursor < to)
        {
            var page = await store.ReadCandlesAsync(id, timeframe, cursor, to, 10000, token);
            result.AddRange(page);
            if (page.Count < 10000) break;
            cursor = new(page[^1].OpenTimeUtc.AddTicks(1), TimeSpan.Zero);
        }
        return result.AsReadOnly();
    }

    private static string Fingerprint(IEnumerable<Candle> candles, IEnumerable<OptionContract> contracts,
        IEnumerable<OptionQuote> quotes)
    {
        var text = new StringBuilder();
        foreach (var candle in candles) text.Append(candle.InstrumentId).Append('|').Append((int)candle.Timeframe).Append('|')
            .Append(candle.OpenTimeUtc.ToString("O", CultureInfo.InvariantCulture)).Append('|')
            .Append(Invariant(candle.Open)).Append('|').Append(Invariant(candle.High)).Append('|')
            .Append(Invariant(candle.Low)).Append('|').Append(Invariant(candle.Close)).Append('|')
            .Append(candle.Volume.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(candle.OpenInterest?.ToString(CultureInfo.InvariantCulture) ?? "null").AppendLine();
        foreach (var contract in contracts.OrderBy(item => item.Id)) text.Append(contract.Id).Append('|')
            .Append(contract.UnderlyingInstrumentId).Append('|').Append(contract.Exchange).Append('|').Append(contract.Symbol).Append('|')
            .Append(contract.ExpiryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('|')
            .Append(Invariant(contract.Strike)).Append('|').Append((int)contract.Right).Append('|')
            .Append(contract.LotSize.ToString(CultureInfo.InvariantCulture)).Append('|').Append(Invariant(contract.TickSize)).AppendLine();
        foreach (var quote in quotes.OrderBy(item => item.TimestampUtc).ThenBy(item => item.OptionContractId))
            text.Append(quote.OptionContractId).Append('|').Append(quote.TimestampUtc.ToString("O", CultureInfo.InvariantCulture)).Append('|')
                .Append(Invariant(quote.Bid)).Append('|').Append(Invariant(quote.Ask)).Append('|').Append(Invariant(quote.Last)).Append('|')
                .Append(quote.Volume.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(quote.OpenInterest.ToString(CultureInfo.InvariantCulture)).AppendLine();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    private static string Invariant(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static async Task WriteNewAsync(string path, string json, CancellationToken token)
    {
        var temporary = $"{path}.tmp-{Guid.NewGuid():N}";
        try { await File.WriteAllTextAsync(temporary, json, new UTF8Encoding(false), token); File.Move(temporary, path, false); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "underlying-id", "timeframe", "from", "to", "strategy-id",
            "initial-capital", "allowed-risk", "maximum-capital", "maximum-lots", "premium-stop-percent", "reward-risk",
            "minimum-volume", "minimum-open-interest", "maximum-spread-bps", "slippage-bps", "cost-profile", "session-exit", "output" };
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Expected a named --option.");
            var key = args[index][2..];
            if (!allowed.Contains(key)) throw new ArgumentException($"Unknown option: --{key}");
            if (++index == args.Length || args[index].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Missing value for --{key}");
            if (!result.TryAdd(key, args[index])) throw new ArgumentException($"Duplicate option: --{key}");
        }
        return result;
    }

    private static string Required(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : throw new ArgumentException($"Missing --{key}");
    private static int PositiveInt(IReadOnlyDictionary<string, string> values, string key) =>
        int.Parse(Required(values, key), NumberStyles.None, CultureInfo.InvariantCulture) is var value && value > 0 ? value : throw new ArgumentException($"--{key} must be positive.");
    private static decimal PositiveDecimal(IReadOnlyDictionary<string, string> values, string key) =>
        decimal.Parse(Required(values, key), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture) is var value && value > 0 ? value : throw new ArgumentException($"--{key} must be positive.");
    private static decimal NonNegativeDecimal(IReadOnlyDictionary<string, string> values, string key) =>
        decimal.Parse(Required(values, key), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture) is var value && value >= 0 ? value : throw new ArgumentException($"--{key} cannot be negative.");
    private static long NonNegativeLong(IReadOnlyDictionary<string, string> values, string key) =>
        long.Parse(Required(values, key), NumberStyles.None, CultureInfo.InvariantCulture) is var value && value >= 0 ? value : throw new ArgumentException($"--{key} cannot be negative.");
    private static decimal Fraction(IReadOnlyDictionary<string, string> values, string key) =>
        PositiveDecimal(values, key) is var value && value < 1 ? value : throw new ArgumentException($"--{key} must be below 1.");
    private static DateTimeOffset Timestamp(IReadOnlyDictionary<string, string> values, string key)
    {
        var value = Required(values, key);
        if (!value.EndsWith('Z') && !(value.Length >= 6 && (value[^6] == '+' || value[^6] == '-') && value[^3] == ':'))
            throw new ArgumentException($"--{key} must include Z or an explicit UTC offset.");
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}
