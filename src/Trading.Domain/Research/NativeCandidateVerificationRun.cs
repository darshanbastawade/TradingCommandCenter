namespace Trading.Domain.Research;

public sealed class NativeCandidateVerificationRun
{
    private NativeCandidateVerificationRun() { }

    public NativeCandidateVerificationRun(Guid id, Guid parameterSweepId, DateTimeOffset createdAt,
        string engineId, string engineVersion, int requestedCandidates, int verifiedCandidates,
        int failedCandidates, string artifactSha256, string artifactJson)
    {
        if (id == Guid.Empty || parameterSweepId == Guid.Empty || requestedCandidates < 1 ||
            verifiedCandidates < 0 || failedCandidates < 0 ||
            verifiedCandidates + failedCandidates != requestedCandidates)
            throw new ArgumentException("Verification identity or counts are invalid.");
        Id = id; ParameterSweepId = parameterSweepId; CreatedAtUtc = createdAt.UtcDateTime;
        EngineId = ParameterSweep.Required(engineId, 64, nameof(engineId));
        EngineVersion = ParameterSweep.Required(engineVersion, 64, nameof(engineVersion));
        RequestedCandidates = requestedCandidates; VerifiedCandidates = verifiedCandidates;
        FailedCandidates = failedCandidates;
        ArtifactSha256 = ParameterSweep.Hash(artifactSha256, nameof(artifactSha256));
        ArtifactJson = ParameterSweep.Json(artifactJson, nameof(artifactJson));
    }

    public Guid Id { get; private set; }
    public Guid ParameterSweepId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public string EngineId { get; private set; } = string.Empty;
    public string EngineVersion { get; private set; } = string.Empty;
    public int RequestedCandidates { get; private set; }
    public int VerifiedCandidates { get; private set; }
    public int FailedCandidates { get; private set; }
    public string ArtifactSha256 { get; private set; } = string.Empty;
    public string ArtifactJson { get; private set; } = string.Empty;
}
