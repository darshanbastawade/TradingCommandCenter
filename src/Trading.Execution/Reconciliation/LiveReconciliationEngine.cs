using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trading.Execution.Reconciliation;

public enum ReconciledOrderStatus { Pending = 1, Submitted = 2, PartiallyFilled = 3, Filled = 4, Cancelled = 5, Rejected = 6 }
public enum LiveReconciliationStatus { Reconciled = 1, Divergent = 2 }

public sealed record ReconciliationPosition(uint InstrumentToken, string Exchange, string TradingSymbol,
    string Product, int Quantity);
public sealed record ReconciliationOrder(Guid RequestId, string BrokerOrderId, uint InstrumentToken,
    string Exchange, string TradingSymbol, ReconciledOrderStatus Status, int OrderedQuantity, int FilledQuantity,
    decimal? AverageFillPrice);
public sealed record LiveReconciliationSnapshot(DateTime AsOfUtc, decimal ExpectedAvailableCash,
    decimal BrokerAvailableCash, decimal CashTolerance, IReadOnlyList<ReconciliationPosition> ExpectedPositions,
    IReadOnlyList<ReconciliationPosition> BrokerPositions, IReadOnlyList<ReconciliationOrder> InternalOrders,
    IReadOnlyList<ReconciliationOrder> BrokerOrders);

public sealed record LiveReconciliationPolicy
{
    public string PolicyVersion { get; init; } = "live-reconciliation-v1";
    public int MaximumSnapshotAgeSeconds { get; init; } = 30;
}

public sealed record LiveReconciliationArtifact(int SchemaVersion, Guid ReconciliationId,
    DateTime ReconciledAtUtc, string PolicyVersion, Guid PaperQualificationId,
    string PaperQualificationSha256, string StrategyId, DateTime BrokerSnapshotAtUtc,
    decimal ExpectedAvailableCash, decimal BrokerAvailableCash, decimal CashDifference,
    int ExpectedPositionCount, int BrokerPositionCount, int InternalOrderCount, int BrokerOrderCount,
    LiveReconciliationStatus Status, IReadOnlyList<string> DiscrepancyCodes,
    bool EligibleForControlledAutomation, string ReconciliationSha256);

public static class LiveReconciliationEngine
{
    private static readonly JsonSerializerOptions Canonical = Options(false);
    private static readonly JsonSerializerOptions Display = Options(true);

    public static LiveReconciliationArtifact Reconcile(Guid paperQualificationId,
        string paperQualificationSha256, string strategyId, LiveReconciliationSnapshot snapshot,
        DateTime reconciledAtUtc, LiveReconciliationPolicy? policy = null)
    {
        policy ??= new(); Validate(paperQualificationId, paperQualificationSha256, strategyId,
            snapshot, reconciledAtUtc, policy);
        var discrepancies = new SortedSet<string>(StringComparer.Ordinal);
        var cashDifference = snapshot.BrokerAvailableCash - snapshot.ExpectedAvailableCash;
        if (Math.Abs(cashDifference) > snapshot.CashTolerance) discrepancies.Add("cash-difference-exceeds-tolerance");
        ComparePositions(snapshot.ExpectedPositions, snapshot.BrokerPositions, discrepancies);
        CompareOrders(snapshot.InternalOrders, snapshot.BrokerOrders, discrepancies);
        var status = discrepancies.Count == 0 ? LiveReconciliationStatus.Reconciled : LiveReconciliationStatus.Divergent;
        var inputHash = Digest(snapshot);
        var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{policy.PolicyVersion}|{paperQualificationSha256}|{inputHash}"))[..16]);
        return Seal(new(1, id, reconciledAtUtc, policy.PolicyVersion.Trim(), paperQualificationId,
            paperQualificationSha256.ToLowerInvariant(), strategyId.Trim(), snapshot.AsOfUtc,
            snapshot.ExpectedAvailableCash, snapshot.BrokerAvailableCash, cashDifference,
            snapshot.ExpectedPositions.Count, snapshot.BrokerPositions.Count, snapshot.InternalOrders.Count,
            snapshot.BrokerOrders.Count, status, discrepancies.ToArray(),
            status == LiveReconciliationStatus.Reconciled, string.Empty));
    }

    public static LiveReconciliationArtifact Seal(LiveReconciliationArtifact value)
    {
        if (value.SchemaVersion != 1 || value.ReconciliationId == Guid.Empty ||
            value.PaperQualificationId == Guid.Empty || !Hash(value.PaperQualificationSha256) ||
            value.ReconciledAtUtc.Kind != DateTimeKind.Utc || value.BrokerSnapshotAtUtc.Kind != DateTimeKind.Utc ||
            value.BrokerSnapshotAtUtc > value.ReconciledAtUtc || string.IsNullOrWhiteSpace(value.PolicyVersion) ||
            string.IsNullOrWhiteSpace(value.StrategyId) || value.ExpectedAvailableCash < 0 ||
            value.BrokerAvailableCash < 0 || value.ExpectedPositionCount < 0 || value.BrokerPositionCount < 0 ||
            value.InternalOrderCount < 0 || value.BrokerOrderCount < 0 || value.DiscrepancyCodes is null ||
            value.DiscrepancyCodes.Any(string.IsNullOrWhiteSpace) ||
            value.DiscrepancyCodes.Distinct(StringComparer.Ordinal).Count() != value.DiscrepancyCodes.Count ||
            !Enum.IsDefined(value.Status) ||
            (value.Status == LiveReconciliationStatus.Reconciled) != (value.DiscrepancyCodes.Count == 0) ||
            value.EligibleForControlledAutomation != (value.Status == LiveReconciliationStatus.Reconciled))
            throw new ArgumentException("Live reconciliation artifact is invalid.", nameof(value));
        var unsigned = value with { ReconciliationSha256 = string.Empty };
        return unsigned with { ReconciliationSha256 = Digest(unsigned) };
    }

    public static bool Verify(LiveReconciliationArtifact value)
    {
        if (value is null || !Hash(value.ReconciliationSha256)) return false;
        try { return Seal(value).ReconciliationSha256 == value.ReconciliationSha256.ToLowerInvariant(); }
        catch (Exception exception) when (exception is ArgumentException or OverflowException) { return false; }
    }
    public static string Serialize(LiveReconciliationArtifact value) => JsonSerializer.Serialize(value, Display);

    private static void ComparePositions(IReadOnlyList<ReconciliationPosition> expected,
        IReadOnlyList<ReconciliationPosition> broker, ISet<string> discrepancies)
    {
        static string Key(ReconciliationPosition x) => $"{x.InstrumentToken}|{x.Exchange}|{x.TradingSymbol}|{x.Product}";
        var left = expected.GroupBy(Key).ToDictionary(x => x.Key, x => x.Sum(y => y.Quantity), StringComparer.Ordinal);
        var right = broker.GroupBy(Key).ToDictionary(x => x.Key, x => x.Sum(y => y.Quantity), StringComparer.Ordinal);
        if (left.Keys.Except(right.Keys).Any()) discrepancies.Add("expected-position-missing-at-broker");
        if (right.Keys.Except(left.Keys).Any()) discrepancies.Add("unexpected-broker-position");
        if (left.Keys.Intersect(right.Keys).Any(key => left[key] != right[key])) discrepancies.Add("position-quantity-mismatch");
    }

    private static void CompareOrders(IReadOnlyList<ReconciliationOrder> internalOrders,
        IReadOnlyList<ReconciliationOrder> brokerOrders, ISet<string> discrepancies)
    {
        var left = internalOrders.ToDictionary(x => x.BrokerOrderId, StringComparer.Ordinal);
        var right = brokerOrders.ToDictionary(x => x.BrokerOrderId, StringComparer.Ordinal);
        if (left.Keys.Except(right.Keys).Any()) discrepancies.Add("internal-order-missing-at-broker");
        if (right.Keys.Except(left.Keys).Any()) discrepancies.Add("unexpected-broker-order");
        foreach (var id in left.Keys.Intersect(right.Keys))
        {
            var a = left[id]; var b = right[id];
            if (a.RequestId != b.RequestId || a.InstrumentToken != b.InstrumentToken || a.Exchange != b.Exchange ||
                a.TradingSymbol != b.TradingSymbol) discrepancies.Add("order-identity-mismatch");
            if (a.Status != b.Status) discrepancies.Add("order-status-mismatch");
            if (a.OrderedQuantity != b.OrderedQuantity || a.FilledQuantity != b.FilledQuantity)
                discrepancies.Add("order-quantity-mismatch");
            if (a.AverageFillPrice != b.AverageFillPrice) discrepancies.Add("order-fill-price-mismatch");
        }
    }

    private static void Validate(Guid id, string hash, string strategyId, LiveReconciliationSnapshot snapshot,
        DateTime now, LiveReconciliationPolicy policy)
    {
        if (id == Guid.Empty || !Hash(hash) || string.IsNullOrWhiteSpace(strategyId) || snapshot is null ||
            now.Kind != DateTimeKind.Utc || snapshot.AsOfUtc.Kind != DateTimeKind.Utc || snapshot.AsOfUtc > now ||
            now - snapshot.AsOfUtc > TimeSpan.FromSeconds(policy.MaximumSnapshotAgeSeconds) ||
            snapshot.ExpectedAvailableCash < 0 || snapshot.BrokerAvailableCash < 0 || snapshot.CashTolerance < 0 ||
            snapshot.ExpectedPositions is null || snapshot.BrokerPositions is null ||
            snapshot.InternalOrders is null || snapshot.BrokerOrders is null ||
            snapshot.InternalOrders.Select(x => x.BrokerOrderId).Distinct(StringComparer.Ordinal).Count() != snapshot.InternalOrders.Count ||
            snapshot.BrokerOrders.Select(x => x.BrokerOrderId).Distinct(StringComparer.Ordinal).Count() != snapshot.BrokerOrders.Count ||
            snapshot.InternalOrders.Concat(snapshot.BrokerOrders).Any(x => x.RequestId == Guid.Empty ||
                string.IsNullOrWhiteSpace(x.BrokerOrderId) || x.InstrumentToken == 0 ||
                string.IsNullOrWhiteSpace(x.Exchange) || string.IsNullOrWhiteSpace(x.TradingSymbol) ||
                x.OrderedQuantity < 1 || x.FilledQuantity < 0 || x.FilledQuantity > x.OrderedQuantity ||
                !Enum.IsDefined(x.Status) || x.AverageFillPrice is <= 0 ||
                (x.FilledQuantity == 0) != (x.AverageFillPrice is null) ||
                (x.Status == ReconciledOrderStatus.Filled && x.FilledQuantity != x.OrderedQuantity) ||
                (x.Status == ReconciledOrderStatus.PartiallyFilled &&
                    (x.FilledQuantity == 0 || x.FilledQuantity == x.OrderedQuantity)) ||
                (x.Status is ReconciledOrderStatus.Pending or ReconciledOrderStatus.Submitted &&
                    x.FilledQuantity != 0)) ||
            snapshot.ExpectedPositions.Concat(snapshot.BrokerPositions).Any(x => x.InstrumentToken == 0 ||
                string.IsNullOrWhiteSpace(x.Exchange) || string.IsNullOrWhiteSpace(x.TradingSymbol) ||
                string.IsNullOrWhiteSpace(x.Product)) || string.IsNullOrWhiteSpace(policy.PolicyVersion) ||
            policy.MaximumSnapshotAgeSeconds is < 1 or > 300)
            throw new ArgumentException("Live reconciliation input or policy is invalid.");
    }

    private static bool Hash(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
    private static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Canonical)))).ToLowerInvariant();
    private static JsonSerializerOptions Options(bool indented)
    { var value = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = indented };
      value.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false)); return value; }
}
