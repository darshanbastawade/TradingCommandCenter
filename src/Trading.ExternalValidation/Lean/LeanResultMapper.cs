using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using Trading.Application.Backtesting;

namespace Trading.ExternalValidation.Lean;

public static class LeanResultMapper
{
    private static readonly JsonSerializerOptions Json = CreateOptions();

    public static BacktestRun Read(LeanProcessResult output, LeanMappedInput input,
        SealedBacktestSpecification specification, string engineVersion)
    {
        var officialDirectory = Path.GetDirectoryName(Path.GetFullPath(output.OfficialResultPath));
        if (!string.Equals(Path.GetFullPath(output.EvidencePath),
                Path.GetFullPath(input.EvidencePath), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(officialDirectory, Path.GetFullPath(input.OutputDirectory),
                StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(output.EvidencePath) || !File.Exists(output.OfficialResultPath) ||
            new FileInfo(output.EvidencePath).Length is < 2 or > 32 * 1024 * 1024 ||
            new FileInfo(output.OfficialResultPath).Length is < 2 or > 64 * 1024 * 1024)
            throw new InvalidDataException("LEAN result evidence is missing, misplaced or too large.");
        using var official = JsonDocument.Parse(File.ReadAllText(output.OfficialResultPath));
        if (official.RootElement.ValueKind != JsonValueKind.Object ||
            !official.RootElement.TryGetProperty("closedTrades", out var closedTrades) ||
            closedTrades.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Official LEAN output lacks a closedTrades array.");
        var run = JsonSerializer.Deserialize<BacktestRun>(File.ReadAllText(output.EvidencePath), Json) ??
            throw new InvalidDataException("LEAN portable evidence is empty.");
        if (run.EngineId != LeanBacktestEngine.Id ||
            run.EngineVersion != engineVersion ||
            run.EngineRole != BacktestEngineRole.IndependentValidation ||
            run.SpecificationSha256 != specification.SpecificationSha256 ||
            run.DeclaredDatasetSha256 != specification.Specification.Data.DatasetSha256 ||
            run.ConsumedMarketDataSha256 != input.ConsumedMarketDataSha256 ||
            run.Trades.Count != closedTrades.GetArrayLength() ||
            !BacktestRunCodec.Verify(run))
            throw new InvalidDataException("LEAN evidence does not match the specification, data or official result.");
        for (var index = 0; index < run.Trades.Count; index++)
        {
            var leanTrade = closedTrades[index];
            var trade = run.Trades[index];
            if (Utc(leanTrade, "entryTime") != trade.EntryTimeUtc ||
                Utc(leanTrade, "exitTime") != trade.ExitTimeUtc ||
                Number(leanTrade, "entryPrice") != trade.EntryPrice ||
                Number(leanTrade, "exitPrice") != trade.ExitPrice ||
                Number(leanTrade, "quantity") != trade.Quantity ||
                Number(leanTrade, "profitLoss") != trade.GrossPnl ||
                Number(leanTrade, "totalFees") != trade.Costs)
                throw new InvalidDataException("LEAN portable trades do not match official closed trades.");
        }
        return run;
    }

    private static DateTime Utc(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            !DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
            throw new InvalidDataException($"Official LEAN trade lacks {name}.");
        return timestamp;
    }

    private static decimal Number(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) ||
            !(value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ||
              value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(),
                  NumberStyles.Number, CultureInfo.InvariantCulture, out number)))
            throw new InvalidDataException($"Official LEAN trade lacks {name}.");
        return number;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false));
        return options;
    }
}
