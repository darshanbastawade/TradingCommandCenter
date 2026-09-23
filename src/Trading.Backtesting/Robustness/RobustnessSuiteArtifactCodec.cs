using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Trading.Backtesting.Robustness;

public static class RobustnessSuiteArtifactCodec
{
    private static readonly JsonSerializerOptions Canonical = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions Display = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static RobustnessSuiteArtifact Seal(RobustnessSuiteArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.SchemaVersion != 1 || artifact.CreatedAtUtc.Kind != DateTimeKind.Utc ||
            artifact.SourceEngineId != "native-csharp" || artifact.OriginalTradeCount < 1 ||
            artifact.InitialCapital <= 0 || !Hash(artifact.SourceResultSha256) ||
            !Hash(artifact.SpecificationSha256) || !Hash(artifact.DatasetSha256) ||
            !Hash(artifact.ConsumedMarketDataSha256) ||
            artifact.Settings is null || artifact.Settings.AdditionalSlippageBasisPointsPerSide is null ||
            artifact.Settings.CostMultipliers is null || artifact.Settings.EntryDelaySeconds is null ||
            artifact.Settings.MissedTradeProbabilities is null ||
            artifact.MonteCarlo is null || artifact.Bootstrap is null ||
            artifact.SlippageStress is null || artifact.CostStress is null ||
            artifact.EntryDelayStress is null || artifact.MissedTradeSimulation is null ||
            artifact.MonteCarlo.Iterations != artifact.Settings.Iterations ||
            artifact.Bootstrap.Iterations != artifact.Settings.Iterations ||
            artifact.SlippageStress.Count != artifact.Settings.AdditionalSlippageBasisPointsPerSide.Count ||
            artifact.CostStress.Count != artifact.Settings.CostMultipliers.Count ||
            artifact.EntryDelayStress.Count != artifact.Settings.EntryDelaySeconds.Count ||
            artifact.MissedTradeSimulation.Count != artifact.Settings.MissedTradeProbabilities.Count ||
            artifact.MissedTradeSimulation.Any(item => item.Distribution.Iterations != artifact.Settings.Iterations))
            throw new ArgumentException("Robustness suite artifact is invalid.", nameof(artifact));
        var unsigned = artifact with { ArtifactSha256 = string.Empty };
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(unsigned, Canonical)))).ToLowerInvariant();
        return unsigned with { ArtifactSha256 = hash };
    }

    public static bool Verify(RobustnessSuiteArtifact artifact)
    {
        if (artifact is null || artifact.ArtifactSha256?.Length != 64) return false;
        try
        {
            return string.Equals(Seal(artifact).ArtifactSha256, artifact.ArtifactSha256,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        { return false; }
    }

    public static string Serialize(RobustnessSuiteArtifact artifact) => JsonSerializer.Serialize(artifact, Display);

    private static bool Hash(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
}
