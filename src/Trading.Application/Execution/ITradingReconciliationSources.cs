using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trading.Domain.Execution;

namespace Trading.Application.Execution;

public sealed record BrokerReconciliationOrder(string BrokerOrderId, uint InstrumentToken,
    string Exchange, string TradingSymbol, string Status, int OrderedQuantity, int FilledQuantity,
    decimal? AverageFillPrice);
public sealed record BrokerReconciliationState(string Provider, string AccountId, DateTime AsOfUtc,
    decimal AvailableCash, IReadOnlyList<BrokerPosition> Positions,
    IReadOnlyList<BrokerReconciliationOrder> Orders);

public interface ILiveBrokerReconciliationClient
{
    Task<BrokerReconciliationState> GetReconciliationStateAsync(
        CancellationToken cancellationToken = default);
}

public sealed record InternalLedgerPosition(uint InstrumentToken, string Exchange, string TradingSymbol,
    string Product, int Quantity);
public sealed record InternalLedgerOrder(Guid RequestId, string BrokerOrderId, uint InstrumentToken,
    string Exchange, string TradingSymbol, string Status, int OrderedQuantity, int FilledQuantity,
    decimal? AverageFillPrice);
public sealed record InternalTradingLedgerArtifact(int SchemaVersion, Guid LedgerStateId,
    DateTime AsOfUtc, string Revision, string BrokerProvider, string BrokerAccountId, string StrategyId,
    decimal ExpectedAvailableCash, decimal ReconciledRealizedPnlToday, int UnresolvedEvents,
    bool Valid, IReadOnlyList<InternalLedgerPosition> Positions, IReadOnlyList<InternalLedgerOrder> Orders,
    string SourceEventsSha256, string ArtifactSha256);
public sealed record DurableLiveOrderEvidence(Guid Id, Guid RequestId, DateTime CreatedAtUtc,
    string Mode, string Status, string StrategyId, string Exchange, string TradingSymbol,
    int Quantity, string BrokerOrderId, string ArtifactSha256, string ArtifactJson);
public sealed record InternalTradingLedgerRead(InternalTradingLedgerArtifact? State,
    IReadOnlyList<DurableLiveOrderEvidence> LiveOrders);

public interface IInternalTradingLedgerReader
{
    Task<InternalTradingLedgerRead> ReadAsync(string strategyId, DateTime asOfUtc,
        CancellationToken cancellationToken = default);
}

public interface IReconciledExecutionStateStore
{
    Task AddAsync(ReconciledExecutionState state, CancellationToken cancellationToken = default);
}

public static class InternalTradingLedgerCodec
{
    private static readonly JsonSerializerOptions Canonical = Options(false);
    private static readonly JsonSerializerOptions Display = Options(true);

    public static InternalTradingLedgerArtifact Seal(InternalTradingLedgerArtifact value)
    {
        if (value.SchemaVersion != 1 || value.LedgerStateId == Guid.Empty ||
            value.AsOfUtc.Kind != DateTimeKind.Utc || string.IsNullOrWhiteSpace(value.Revision) ||
            value.Revision.Length > 128 || string.IsNullOrWhiteSpace(value.BrokerProvider) ||
            string.IsNullOrWhiteSpace(value.BrokerAccountId) || string.IsNullOrWhiteSpace(value.StrategyId) ||
            value.ExpectedAvailableCash < 0 || value.UnresolvedEvents < 0 || value.Positions is null ||
            value.Orders is null || !Hash(value.SourceEventsSha256) ||
            value.Positions.Any(item => item.InstrumentToken == 0 || string.IsNullOrWhiteSpace(item.Exchange) ||
                string.IsNullOrWhiteSpace(item.TradingSymbol) || string.IsNullOrWhiteSpace(item.Product)) ||
            value.Orders.Any(item => item.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(item.BrokerOrderId) ||
                item.InstrumentToken == 0 || string.IsNullOrWhiteSpace(item.Exchange) ||
                string.IsNullOrWhiteSpace(item.TradingSymbol) || string.IsNullOrWhiteSpace(item.Status) ||
                item.OrderedQuantity < 1 || item.FilledQuantity < 0 || item.FilledQuantity > item.OrderedQuantity ||
                (item.FilledQuantity == 0) != (item.AverageFillPrice is null) || item.AverageFillPrice is <= 0) ||
            value.Orders.Select(item => item.BrokerOrderId).Distinct(StringComparer.Ordinal).Count() != value.Orders.Count)
            throw new ArgumentException("Internal trading-ledger artifact is invalid.", nameof(value));
        var unsigned = value with { ArtifactSha256 = string.Empty };
        return unsigned with { ArtifactSha256 = Digest(unsigned) };
    }

    public static bool Verify(InternalTradingLedgerArtifact value)
    {
        if (value is null || !Hash(value.ArtifactSha256)) return false;
        try { return Seal(value).ArtifactSha256 == value.ArtifactSha256.ToLowerInvariant(); }
        catch (Exception exception) when (exception is ArgumentException or OverflowException) { return false; }
    }

    public static string Serialize(InternalTradingLedgerArtifact value) =>
        JsonSerializer.Serialize(value, Display);
    public static InternalTradingLedgerArtifact Deserialize(string value) =>
        JsonSerializer.Deserialize<InternalTradingLedgerArtifact>(value, Display) ??
        throw new InvalidDataException("Internal trading-ledger JSON is empty.");
    private static bool Hash(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
    private static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Canonical)))).ToLowerInvariant();
    private static JsonSerializerOptions Options(bool indented)
    {
        var value = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = indented };
        value.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false));
        return value;
    }
}
