using Microsoft.EntityFrameworkCore;
using Trading.Application.Research;
using Trading.Domain.Research;

namespace Trading.Infrastructure.Persistence;

public sealed class StrategyCertificateStore(TradingDbContext db) : IStrategyCertificateStore
{
    public async Task AddRangeAsync(IReadOnlyList<IssuedStrategyCertificate> certificates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(certificates);
        if (certificates.Count == 0) return;
        await db.StrategyCertificates.AddRangeAsync(certificates, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<IssuedStrategyCertificate?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.StrategyCertificates.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);

    public async Task<IReadOnlyList<StrategyCertificateSummary>> ListAsync(int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        return await db.StrategyCertificates.AsNoTracking().OrderByDescending(item => item.IssuedAtUtc)
            .ThenBy(item => item.StrategyId).Take(limit)
            .Select(item => new StrategyCertificateSummary(item.Id, item.ResearchRunId, item.StrategyId,
                item.IssuedAtUtc, item.ExpiresAtUtc, item.Status, item.ResearchArtifactSha256,
                item.CertificateSha256)).ToListAsync(cancellationToken);
    }
}
