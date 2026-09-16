using Trading.Domain.MarketData;

namespace Trading.Application.MarketData;

public sealed record MarketFeedCaptureSummary(Guid Id, DateTime CreatedAtUtc, string Source, string QuoteMode,
    int TickCount, DateTime FirstReceivedAtUtc, DateTime LastReceivedAtUtc, string ArtifactSha256);

public interface IMarketFeedCaptureStore
{
    Task AddAsync(MarketFeedCapture capture, CancellationToken cancellationToken = default);
    Task<MarketFeedCapture?> FindAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MarketFeedCaptureSummary>> ListAsync(int limit = 100,
        CancellationToken cancellationToken = default);
}
