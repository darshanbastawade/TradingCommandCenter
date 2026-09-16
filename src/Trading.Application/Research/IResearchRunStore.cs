using Trading.Domain.Research;
using Trading.Domain.MarketData;

namespace Trading.Application.Research;

public sealed record ResearchRunSummary(
    Guid Id,
    DateTime CreatedAtUtc,
    Guid InstrumentId,
    Timeframe Timeframe,
    DateTime FromUtc,
    DateTime ToUtc,
    string DataSource,
    string DataVersion,
    string CalendarId,
    string DatasetSha256,
    string ConfigurationSha256,
    string ArtifactSha256,
    string SourceRevision);

public interface IResearchRunStore
{
    Task AddAsync(ResearchRun run, CancellationToken cancellationToken = default);
    Task<ResearchRun?> FindAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ResearchRunSummary>> ListAsync(int limit = 100, CancellationToken cancellationToken = default);
}
