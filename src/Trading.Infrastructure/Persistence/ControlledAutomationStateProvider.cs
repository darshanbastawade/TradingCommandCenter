using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Trading.Application.Execution;

namespace Trading.Infrastructure.Persistence;

public sealed class ControlledAutomationStateProvider(TradingDbContext db) : IControlledAutomationStateProvider
{
    public async Task<ControlledAutomationStateSnapshot> GetAsync(DateTime evaluatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (evaluatedAtUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("Evaluation time must be UTC.");
        var india = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(evaluatedAtUtc, india));
        var start = TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue), india);
        var end = TimeZoneInfo.ConvertTimeToUtc(date.AddDays(1).ToDateTime(TimeOnly.MinValue), india);
        var consumed = await db.ControlledAutomationAuthorizations.AsNoTracking().CountAsync(item =>
            item.FirstConsumedAtUtc >= start && item.FirstConsumedAtUtc < end, cancellationToken);
        var submitted = await db.LiveOrders.AsNoTracking().CountAsync(item => item.Mode == "DirectLive" &&
            item.Status == "Submitted" && item.CreatedAtUtc >= start && item.CreatedAtUtc < end, cancellationToken);
        var reconciled = await db.ReconciledExecutionStates.AsNoTracking().Where(item =>
            item.ExchangeTradingDate == date && item.AsOfUtc <= evaluatedAtUtc)
            .OrderByDescending(item => item.AsOfUtc).FirstOrDefaultAsync(cancellationToken);
        if (reconciled is null || !reconciled.Authoritative)
            return new(evaluatedAtUtc, date, false, consumed, submitted, 0, 0, 0,
                string.Empty, "authoritative-reconciled-state-unavailable");
        var laterUnresolved = await db.LiveOrders.AsNoTracking().CountAsync(item => item.Mode == "DirectLive" &&
            (item.Status == "Prepared" || item.Status == "Submitted") && item.CreatedAtUtc > reconciled.AsOfUtc &&
            item.CreatedAtUtc <= evaluatedAtUtc, cancellationToken);
        var evidencePayload = string.Join('|', reconciled.ReconciliationSha256, reconciled.SourceRevision,
            date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            consumed.ToString(CultureInfo.InvariantCulture), submitted.ToString(CultureInfo.InvariantCulture),
            reconciled.RealizedPnlToday.ToString(CultureInfo.InvariantCulture),
            reconciled.UnresolvedBrokerSubmissions.ToString(CultureInfo.InvariantCulture),
            reconciled.ActiveOpenBrokerPositions.ToString(CultureInfo.InvariantCulture),
            laterUnresolved.ToString(CultureInfo.InvariantCulture));
        var evidence = Sha256(evidencePayload);
        return new(reconciled.AsOfUtc, date, true, consumed, submitted, reconciled.RealizedPnlToday,
            reconciled.UnresolvedBrokerSubmissions + laterUnresolved,
            reconciled.ActiveOpenBrokerPositions, evidence, string.Empty);
    }
    private static string Sha256(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
