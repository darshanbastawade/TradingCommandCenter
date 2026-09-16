using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trading.Application.Backtesting;

public static class BacktestRunCodec
{
    public const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions CanonicalJson = CreateOptions(false);
    private static readonly JsonSerializerOptions DisplayJson = CreateOptions(true);

    public static BacktestRun Seal(BacktestRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.SchemaVersion != CurrentSchemaVersion || string.IsNullOrWhiteSpace(run.EngineId) ||
            string.IsNullOrWhiteSpace(run.EngineVersion) || !Enum.IsDefined(run.EngineRole) ||
            !Hash(run.SpecificationSha256) || !Hash(run.DeclaredDatasetSha256) ||
            !Hash(run.ConsumedMarketDataSha256) || run.InitialCapital <= 0 ||
            run.Trades is null || run.IgnoredCandidates is null ||
            run.FinalCapital != run.InitialCapital + run.NetPnl || run.WinningTrades < 0 || run.LosingTrades < 0 ||
            run.WinningTrades != run.Trades.Count(item => item.NetPnl > 0) ||
            run.LosingTrades != run.Trades.Count(item => item.NetPnl < 0) ||
            run.NetPnl != run.Trades.Sum(item => item.NetPnl))
            throw new ArgumentException("Backtest run identity or summary is invalid.", nameof(run));
        ValidateTrades(run);
        var unsigned = run with
        {
            EngineId = run.EngineId.Trim(),
            EngineVersion = run.EngineVersion.Trim(),
            ResultSha256 = string.Empty
        };
        var canonical = JsonSerializer.Serialize(unsigned, CanonicalJson);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return unsigned with { ResultSha256 = hash };
    }

    public static bool Verify(BacktestRun run)
    {
        if (run is null || !Hash(run.ResultSha256)) return false;
        try
        {
            var sealedRun = Seal(run);
            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(sealedRun.ResultSha256),
                Encoding.ASCII.GetBytes(run.ResultSha256.ToLowerInvariant()));
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException) { return false; }
    }

    public static string Serialize(BacktestRun run) => JsonSerializer.Serialize(run, DisplayJson);

    private static void ValidateTrades(BacktestRun run)
    {
        DateTime? previousEntry = null;
        var expectedCapital = run.InitialCapital;
        foreach (var trade in run.Trades)
        {
            expectedCapital += trade.NetPnl;
            if (string.IsNullOrWhiteSpace(trade.StrategyId) || trade.InstrumentId == Guid.Empty ||
                !Enum.IsDefined(trade.Direction) || trade.SignalTimeUtc.Kind != DateTimeKind.Utc ||
                trade.EntryTimeUtc.Kind != DateTimeKind.Utc || trade.ExitTimeUtc.Kind != DateTimeKind.Utc ||
                trade.SignalTimeUtc >= trade.EntryTimeUtc || trade.EntryTimeUtc > trade.ExitTimeUtc ||
                trade.Quantity < 1 || trade.EntryPrice <= 0 || trade.StopPrice <= 0 || trade.TargetPrice <= 0 ||
                trade.ExitPrice <= 0 || string.IsNullOrWhiteSpace(trade.ExitReason) || trade.Costs < 0 ||
                trade.NetPnl != trade.GrossPnl - trade.Costs || trade.CapitalAfterTrade != expectedCapital ||
                previousEntry is not null && trade.EntryTimeUtc < previousEntry)
                throw new ArgumentException("Backtest run contains invalid trade evidence.", nameof(run));
            previousEntry = trade.EntryTimeUtc;
        }
        foreach (var ignored in run.IgnoredCandidates)
            if (ignored.SignalTimeUtc.Kind != DateTimeKind.Utc || string.IsNullOrWhiteSpace(ignored.Reason))
                throw new ArgumentException("Backtest run contains invalid ignored-candidate evidence.", nameof(run));
    }

    private static bool Hash(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);

    private static JsonSerializerOptions CreateOptions(bool indented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = indented };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
