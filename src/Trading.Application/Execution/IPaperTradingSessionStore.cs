using Trading.Domain.Execution;

namespace Trading.Application.Execution;

public sealed record PaperTradingSessionSummary(Guid Id, Guid StrategyCertificateId, Guid MarketFeedCaptureId,
    DateTime CreatedAtUtc, string StrategyId, decimal InitialCash, decimal EndingCash,
    decimal RealizedNetPnl, int SubmittedOrders, int FilledTrades, int RejectedOrders,
    string ConfigurationSha256, string ArtifactSha256);

public interface IPaperTradingSessionStore
{
    Task AddAsync(PaperTradingSession session, CancellationToken cancellationToken = default);
    Task<PaperTradingSession?> FindAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PaperTradingSessionSummary>> ListAsync(int limit = 100,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PaperTradingSession>> ListForCertificateAsync(Guid certificateId,
        CancellationToken cancellationToken = default);
}
