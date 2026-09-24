using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Trading.ExternalValidation.Lean;

public sealed record LeanAlgorithmProjectEvidence(string StrategyId, string StrategyImplementationVersion,
    string AlgorithmSourceRevision);

public static class LeanAlgorithmProject
{
    private sealed record Manifest(int SchemaVersion, string StrategyId,
        string StrategyImplementationVersion, string AlgorithmSourceRevision);

    public static LeanAlgorithmProjectEvidence Verify(string projectDirectory, string strategyId)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory) || !Directory.Exists(projectDirectory))
            throw new InvalidOperationException("LEAN project directory is not configured or does not exist.");
        var sourcePath = Path.Combine(projectDirectory, "main.py");
        var manifestPath = Path.Combine(projectDirectory, "tcc-strategy-manifest.json");
        if (!File.Exists(sourcePath) || !File.Exists(manifestPath))
            throw new InvalidDataException("LEAN project lacks main.py or its strategy manifest.");
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ??
            throw new InvalidDataException("LEAN strategy manifest is empty.");
        var source = File.ReadAllText(sourcePath, Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal);
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        if (manifest.SchemaVersion != 1 || manifest.StrategyId != strategyId ||
            string.IsNullOrWhiteSpace(manifest.StrategyImplementationVersion) ||
            !string.Equals(manifest.AlgorithmSourceRevision, revision, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("LEAN strategy manifest is stale or does not match the request.");
        return new(strategyId, manifest.StrategyImplementationVersion, revision);
    }
}
