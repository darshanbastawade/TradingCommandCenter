using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trading.Application.AI;
using Trading.Backtesting.Certification;
using Trading.Backtesting.Comparison;
using Trading.Backtesting.Reporting;
using Trading.Backtesting.Robustness;

namespace Trading.Api;

public static class AstraResearchAnalystV2Commands
{
    private static readonly JsonSerializerOptions Json = Options();
    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] == "analyze-research-v2";

    public static async Task<int> RunAsync(string[] args, IServiceProvider services, TextWriter output,
        TextWriter error, CancellationToken cancellationToken = default)
    {
        try
        {
            var command = Parse(args);
            var certificateJson = await ReadAsync(command.Certificate, 2 * 1024 * 1024, cancellationToken);
            var researchJson = await ReadAsync(command.Research, 32 * 1024 * 1024, cancellationToken);
            var comparisonJson = await ReadAsync(command.Comparison, 64 * 1024 * 1024, cancellationToken);
            var robustnessJson = await ReadAsync(command.Robustness, 32 * 1024 * 1024, cancellationToken);
            var certificate = Deserialize<StrategyCertificateV2>(certificateJson);
            var research = Deserialize<ResearchIntegrityArtifact>(researchJson);
            var comparison = Deserialize<CrossEngineComparison>(comparisonJson);
            var robustness = Deserialize<RobustnessSuiteArtifact>(robustnessJson);
            if (!StrategyCertificateV2Issuer.Verify(certificate) ||
                !CrossEngineComparisonCodec.Verify(comparison) ||
                !RobustnessSuiteArtifactCodec.Verify(robustness) ||
                certificate.ResearchRunId != research.RunId ||
                certificate.ResearchArtifactSha256 != Sha256(researchJson) ||
                certificate.CrossEngineComparisonSha256 != comparison.ComparisonSha256 ||
                certificate.RobustnessArtifactSha256 != robustness.ArtifactSha256 ||
                research.Strategies.All(item => item.StrategyId != certificate.StrategyId))
                throw new InvalidDataException("Astra V2 evidence identity or hash is invalid.");
            var bundleJson = JsonSerializer.Serialize(new { certificate, research, comparison, robustness }, Json);
            var bundleHash = Sha256(bundleJson);
            await using var scope = services.CreateAsyncScope();
            var analyst = scope.ServiceProvider.GetRequiredService<IAstraResearchAnalystV2>();
            var response = await analyst.AnalyzeAsync(new(certificate.CertificateId, certificate.StrategyId,
                certificate.CertificateSha256, bundleHash, bundleJson), cancellationToken);
            var artifact = AstraResearchAnalysisV2Codec.Seal(new(2, Guid.NewGuid(), DateTime.UtcNow,
                certificate.CertificateId, certificate.StrategyId, certificate.CertificateSha256, bundleHash,
                analyst.Deployment, response.ResponseModel, response.ProviderResponseId, analyst.PromptVersion,
                analyst.PromptSha256, response.InputTokens, response.OutputTokens, response.Output, false,
                string.Empty));
            await WriteAsync(command.Output, AstraResearchAnalysisV2Codec.Serialize(artifact), cancellationToken);
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                status = "astra-research-analysis-v2-created", artifact.AnalysisId,
                artifact.CertificateId, artifact.StrategyId, artifact.Analysis.Recommendation,
                artifact.AnalysisSha256, output = command.Output
            }, Json));
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { await error.WriteLineAsync("Astra research analysis V2 cancelled."); return 130; }
        catch (HttpRequestException exception)
        { await error.WriteLineAsync(exception.Message); return 3; }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or
                                           InvalidOperationException or IOException or JsonException or
                                           UnauthorizedAccessException)
        { await error.WriteLineAsync(exception.Message); return 2; }
    }

    private static Command Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {args[index]}.");
            if (args[index] is not ("--certificate" or "--research" or "--comparison" or "--robustness" or
                "--output") || !values.TryAdd(args[index], args[index + 1]))
                throw new ArgumentException($"Unknown or duplicate option: {args[index]}");
        }
        return new(Input(values, "--certificate"), Input(values, "--research"),
            Input(values, "--comparison"), Input(values, "--robustness"), Output(values));
    }

    private static string Input(IReadOnlyDictionary<string, string> values, string key)
    {
        var path = PathValue(values, key);
        if (!File.Exists(path)) throw new FileNotFoundException($"{key} file was not found.", path);
        return path;
    }
    private static string Output(IReadOnlyDictionary<string, string> values)
    {
        var path = PathValue(values, "--output");
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw new ArgumentException("Output directory does not exist.");
        if (File.Exists(path)) throw new IOException("Analysis V2 output already exists.");
        return path;
    }
    private static string PathValue(IReadOnlyDictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var value)) throw new ArgumentException($"{key} is required.");
        var path = Path.GetFullPath(value);
        if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{key} must use .json.");
        return path;
    }
    private static async Task<string> ReadAsync(string path, long maximum, CancellationToken token)
    {
        var length = new FileInfo(path).Length;
        if (length is < 2 || length > maximum) throw new InvalidDataException("Analysis evidence is empty or too large.");
        return await File.ReadAllTextAsync(path, token);
    }
    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Json) ??
        throw new InvalidDataException("Analysis evidence JSON is empty.");
    private static async Task WriteAsync(string path, string value, CancellationToken token)
    {
        var temporary = $"{path}.tmp-{Guid.NewGuid():N}";
        try { await File.WriteAllTextAsync(temporary, value, new UTF8Encoding(false), token); File.Move(temporary, path); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false));
        return options;
    }
    private sealed record Command(string Certificate, string Research, string Comparison,
        string Robustness, string Output);
}
