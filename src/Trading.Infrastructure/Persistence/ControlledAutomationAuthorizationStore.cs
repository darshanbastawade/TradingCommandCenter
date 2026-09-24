using Microsoft.EntityFrameworkCore;
using Trading.Application.Execution;
using Trading.Domain.Execution;

namespace Trading.Infrastructure.Persistence;

public sealed class ControlledAutomationAuthorizationStore(TradingDbContext db) : IControlledAutomationAuthorizationStore
{
    public async Task<bool> TryConsumeAsync(ControlledAutomationReservation value, DateTime consumedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (await db.ControlledAutomationAuthorizations.AsNoTracking().AnyAsync(item =>
                item.AutomationDecisionId == value.AutomationDecisionId ||
                item.AutomationSha256 == value.AutomationSha256 || item.ActionId == value.ActionId,
                cancellationToken))
            return false;
        var entity = new ControlledAutomationAuthorization(value.AutomationDecisionId,
            value.AutomationSha256, value.ActionId, value.ActionReference, value.StrategyId,
            value.EvaluatedAtUtc, value.ExpiresAtUtc, value.Decision, value.MaximumAuthorizedActions,
            consumedAtUtc, value.RelatedLiveOrderId);
        db.ControlledAutomationAuthorizations.Add(entity);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            db.Entry(entity).State = EntityState.Detached;
            return false;
        }
    }

    public Task<bool> IsConsumedAsync(Guid automationDecisionId, CancellationToken cancellationToken = default) =>
        db.ControlledAutomationAuthorizations.AsNoTracking()
            .AnyAsync(item => item.AutomationDecisionId == automationDecisionId, cancellationToken);
}
