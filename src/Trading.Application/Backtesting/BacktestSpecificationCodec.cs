using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using Trading.Domain.MarketData;

namespace Trading.Application.Backtesting;

public static class BacktestSpecificationCodec
{
    public const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions CanonicalJson = CreateOptions(false);
    private static readonly JsonSerializerOptions DisplayJson = CreateOptions(true);

    public static BacktestSpecification Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("Backtest specification JSON is empty.", nameof(json));
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32
        });
        RejectDuplicateProperties(document.RootElement, "$");
        return JsonSerializer.Deserialize<BacktestSpecification>(json, CanonicalJson) ??
            throw new JsonException("Backtest specification JSON is empty.");
    }

    public static SealedBacktestSpecification DeserializeSealed(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("Sealed backtest specification JSON is empty.", nameof(json));
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32
        });
        RejectDuplicateProperties(document.RootElement, "$");
        return JsonSerializer.Deserialize<SealedBacktestSpecification>(json, CanonicalJson) ??
            throw new JsonException("Sealed backtest specification JSON is empty.");
    }

    public static SealedBacktestSpecification Seal(BacktestSpecification specification)
    {
        var normalized = NormalizeAndValidate(specification);
        var canonical = JsonSerializer.Serialize(normalized, CanonicalJson);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new(normalized, hash);
    }

    public static string Serialize(SealedBacktestSpecification document) =>
        JsonSerializer.Serialize(document, DisplayJson);

    public static bool Verify(SealedBacktestSpecification document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var resealed = Seal(document.Specification);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(resealed.SpecificationSha256),
            Encoding.ASCII.GetBytes(document.SpecificationSha256?.ToLowerInvariant() ?? string.Empty));
    }

    public static BacktestSpecification NormalizeAndValidate(BacktestSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        if (specification.SchemaVersion != CurrentSchemaVersion)
            throw new ArgumentException($"schemaVersion must be {CurrentSchemaVersion}.", nameof(specification));
        ArgumentNullException.ThrowIfNull(specification.Instrument);
        ArgumentNullException.ThrowIfNull(specification.Data);
        ArgumentNullException.ThrowIfNull(specification.Capital);
        ArgumentNullException.ThrowIfNull(specification.Execution);
        ArgumentNullException.ThrowIfNull(specification.Parameters);

        var strategyId = Required(specification.StrategyId, 128, "strategyId");
        var instrument = specification.Instrument with
        {
            Exchange = Required(specification.Instrument.Exchange, 16, "instrument.exchange"),
            TradingSymbol = Required(specification.Instrument.TradingSymbol, 96, "instrument.tradingSymbol"),
            Currency = Required(specification.Instrument.Currency, 8, "instrument.currency"),
            TickSize = NormalizeDecimal(specification.Instrument.TickSize)
        };
        if (instrument.InstrumentId == Guid.Empty || !Enum.IsDefined(instrument.AssetClass) ||
            instrument.LotSize < 1 || instrument.TickSize <= 0)
            throw new ArgumentException("Instrument identity, asset class, lot size, and tick size must be valid.", nameof(specification));

        var data = specification.Data with
        {
            ExchangeTimeZoneId = Required(specification.Data.ExchangeTimeZoneId, 128, "data.exchangeTimeZoneId"),
            CalendarId = Required(specification.Data.CalendarId, 64, "data.calendarId"),
            Source = Required(specification.Data.Source, 64, "data.source"),
            Version = Required(specification.Data.Version, 64, "data.version"),
            DatasetSha256 = Hash(specification.Data.DatasetSha256, "data.datasetSha256")
        };
        if (data.FromUtc.Kind != DateTimeKind.Utc || data.ToUtc.Kind != DateTimeKind.Utc || data.FromUtc >= data.ToUtc)
            throw new ArgumentException("Data range must be a non-empty UTC half-open interval.", nameof(specification));
        if (!Enum.IsDefined(typeof(Timeframe), data.TimeframeMinutes) || !Enum.IsDefined(data.MarketDataMode))
            throw new ArgumentException("Timeframe or market-data mode is unsupported.", nameof(specification));
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(data.ExchangeTimeZoneId); }
        catch (TimeZoneNotFoundException exception) { throw new ArgumentException("Exchange time-zone ID is unavailable.", nameof(specification), exception); }
        catch (InvalidTimeZoneException exception) { throw new ArgumentException("Exchange time-zone ID is invalid.", nameof(specification), exception); }

        var capital = specification.Capital with
        {
            InitialCapital = NormalizeDecimal(specification.Capital.InitialCapital),
            RiskPerTrade = NormalizeDecimal(specification.Capital.RiskPerTrade),
            MaximumCapitalPerTrade = NormalizeDecimal(specification.Capital.MaximumCapitalPerTrade)
        };
        if (capital.InitialCapital <= 0 || capital.RiskPerTrade <= 0 || capital.MaximumCapitalPerTrade <= 0 ||
            capital.RiskPerTrade > capital.InitialCapital || capital.MaximumCapitalPerTrade > capital.InitialCapital ||
            capital.MaximumLots is <= 0)
            throw new ArgumentException("Capital and risk settings are invalid.", nameof(specification));

        var execution = specification.Execution with
        {
            RewardRiskMultiple = NormalizeDecimal(specification.Execution.RewardRiskMultiple),
            SlippageBasisPointsPerSide = NormalizeDecimal(specification.Execution.SlippageBasisPointsPerSide),
            CostProfileId = Required(specification.Execution.CostProfileId, 96, "execution.costProfileId")
        };
        if (execution.RewardRiskMultiple <= 0 || execution.RewardRiskMultiple > 20 ||
            execution.SlippageBasisPointsPerSide < 0 || execution.SlippageBasisPointsPerSide >= 10_000 ||
            !Enum.IsDefined(execution.SignalTiming) || !Enum.IsDefined(execution.EntryFillPolicy) ||
            !Enum.IsDefined(execution.AmbiguousBarPolicy) || !Enum.IsDefined(execution.EndOfDataPolicy))
            throw new ArgumentException("Execution assumptions are invalid.", nameof(specification));
        if (data.MarketDataMode == BacktestMarketDataMode.OhlcvBars &&
            execution.EntryFillPolicy != BacktestEntryFillPolicy.NextObservedBarOpen)
            throw new ArgumentException("OHLCV mode requires next-observed-bar-open entry fills.", nameof(specification));
        if (data.MarketDataMode == BacktestMarketDataMode.ObservedOptionQuotes &&
            execution.EntryFillPolicy != BacktestEntryFillPolicy.ObservedAsk)
            throw new ArgumentException("Observed-option mode requires observed-ask entry fills.", nameof(specification));

        if (specification.Parameters.Count > 128)
            throw new ArgumentException("At most 128 strategy parameters are supported.", nameof(specification));
        var parameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parameters = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var item in specification.Parameters)
        {
            var name = Required(item.Key, 64, "parameter name");
            if (!name.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
                throw new ArgumentException($"Parameter name '{name}' contains unsupported characters.", nameof(specification));
            if (!parameterNames.Add(name))
                throw new ArgumentException($"Parameter name '{name}' is duplicated by case.", nameof(specification));
            parameters.Add(name, NormalizeDecimal(item.Value));
        }

        return specification with
        {
            StrategyId = strategyId,
            Instrument = instrument,
            Data = data,
            Capital = capital,
            Execution = execution,
            Parameters = parameters
        };
    }

    private static void RejectDuplicateProperties(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException($"Duplicate JSON property at {path}.{property.Name}.");
                RejectDuplicateProperties(property.Value, $"{path}.{property.Name}");
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item, $"{path}[{index++}]");
        }
    }

    private static string Required(string value, int maximum, string field)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is 0 || normalized.Length > maximum || normalized.Any(char.IsControl))
            throw new ArgumentException($"{field} must contain 1-{maximum} non-control characters.");
        return normalized;
    }

    private static string Hash(string value, string field)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException($"{field} must be a SHA-256 value.");
        return normalized;
    }

    private static decimal NormalizeDecimal(decimal value) =>
        decimal.Parse(value.ToString("G29", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static JsonSerializerOptions CreateOptions(bool indented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = indented,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
