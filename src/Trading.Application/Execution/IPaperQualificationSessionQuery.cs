using Trading.Domain.Execution;

namespace Trading.Application.Execution;

public interface IPaperQualificationSessionQuery
{
    Task<IReadOnlyList<PaperTradingSession>> ListForQualificationAsync(Guid qualificationId,
        DateTime observationStartUtc, DateTime observationCutoffUtc,
        CancellationToken cancellationToken = default);
}
