using Trading.Domain.Research;

namespace Trading.Application.Research;

public sealed record StrategyCertificateSummary(Guid Id, Guid ResearchRunId, string StrategyId,
    DateTime IssuedAtUtc, DateTime ExpiresAtUtc, string Status, string ResearchArtifactSha256,
    string CertificateSha256);

public interface IStrategyCertificateStore
{
    Task AddRangeAsync(IReadOnlyList<IssuedStrategyCertificate> certificates,
        CancellationToken cancellationToken = default);
    Task<IssuedStrategyCertificate?> FindAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StrategyCertificateSummary>> ListAsync(int limit = 100,
        CancellationToken cancellationToken = default);
}
