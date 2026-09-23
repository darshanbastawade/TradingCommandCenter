using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trading.Application.Backtesting;
using Trading.Backtesting.Certification;
using Trading.Backtesting.Comparison;
using Trading.Backtesting.Reporting;
using Trading.Backtesting.Robustness;

namespace Trading.Api;

public static class StrategyCertificateV2Commands
{
    private static readonly JsonSerializerOptions Json = Options();
    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] == "issue-certificate-v2";

    public static async Task<int> RunAsync(string[] args, IConfiguration configuration, TextWriter output,
        TextWriter error, CancellationToken cancellationToken = default)
    {
        try
        {
            var command = Parse(args);
            var researchJson = await ReadTextAsync(command.Research, 32 * 1024 * 1024, cancellationToken);
            var research = Deserialize<ResearchIntegrityArtifact>(researchJson);
            var specification = BacktestSpecificationCodec.DeserializeSealed(
                await ReadTextAsync(command.Specification, 1024 * 1024, cancellationToken));
            var native = Deserialize<BacktestRun>(await ReadTextAsync(command.Native, 32 * 1024 * 1024, cancellationToken));
            var comparison = Deserialize<CrossEngineComparison>(await ReadTextAsync(command.Comparison,
                64 * 1024 * 1024, cancellationToken));
            var robustness = Deserialize<RobustnessSuiteArtifact>(await ReadTextAsync(command.Robustness,
                32 * 1024 * 1024, cancellationToken));
            ValidateEvidence(research, specification, native, comparison, robustness);
            var strategyId = specification.Specification.StrategyId;
            var score = research.Ranking.SelectedStrategies.SingleOrDefault(item => item.StrategyId == strategyId) ??
                throw new InvalidDataException("The specification strategy is not selected by the research ranking.");
            if (research.Strategies.All(item => item.StrategyId != strategyId))
                throw new InvalidDataException("The research artifact lacks evidence for the selected strategy.");
            var policy = configuration.GetSection("StrategyCertificateV2").Get<StrategyCertificateV2Policy>() ?? new();
            var slippage = AtLeast(robustness.SlippageStress,
                policy.RequiredAdditionalSlippageBasisPointsPerSide, "basisPointsPerSide", "slippage").NetPnl;
            var cost = AtLeast(robustness.CostStress, policy.RequiredCostMultiplier,
                "multiplier", "cost").NetPnl;
            var delay = AtLeast(robustness.EntryDelayStress, policy.RequiredEntryDelaySeconds,
                "seconds", "entry delay").NetPnl;
            var missed = robustness.MissedTradeSimulation
                .Where(item => item.MissedTradeProbability >= policy.RequiredMissedTradeProbability)
                .OrderBy(item => item.MissedTradeProbability).FirstOrDefault() ??
                throw new InvalidDataException("Robustness evidence lacks the required missed-trade scenario.");
            var source = new StrategyCertificateV2Source(research.RunId, research.CreatedAtUtc,
                strategyId, research.SourceRevision, research.Dataset.DatasetSha256, Sha256(researchJson),
                specification.SpecificationSha256, ParametersHash(specification.Specification.Parameters),
                native.ResultSha256, comparison.LeanResultSha256, comparison.ComparisonSha256,
                robustness.ArtifactSha256, score, comparison.Verdict == CrossEngineVerdict.Pass,
                comparison.MatchedTradeRate, comparison.EntryTimestampMatchRate,
                comparison.ExitTimestampMatchRate,
                Math.Max(comparison.MaximumEntryPriceDeviationBasisPoints,
                    comparison.MaximumExitPriceDeviationBasisPoints),
                comparison.NetPnlDeviationPercent, comparison.DrawdownDeviationPercentagePoints,
                robustness.MonteCarlo.NinetyFifthPercentileMaximumDrawdownPercent,
                robustness.Bootstrap.ProbabilityOfLoss, robustness.Bootstrap.ProbabilityOfRuin,
                robustness.Bootstrap.FifthPercentileNetPnl, slippage, cost, delay,
                missed.Distribution.MedianNetPnl);
            var certificate = StrategyCertificateV2Issuer.Issue(source, policy);
            await WriteNewAtomicallyAsync(command.Output, StrategyCertificateV2Issuer.Serialize(certificate),
                cancellationToken);
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                status = "strategy-certificate-v2-issued", certificate.CertificateId,
                certificate.StrategyId, certificate.Status, certificate.EligibleForQualificationPipeline,
                certificate.EvidenceFailures, certificate.CertificateSha256, output = command.Output
            }, Json));
            return certificate.Status == StrategyCertificateV2Status.EvidenceQualified ? 0 : 4;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { await error.WriteLineAsync("Certificate V2 issuance cancelled."); return 130; }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or
                                           IOException or JsonException or UnauthorizedAccessException)
        { await error.WriteLineAsync(exception.Message); return 2; }
    }

    private static void ValidateEvidence(ResearchIntegrityArtifact research,
        SealedBacktestSpecification specification, BacktestRun native, CrossEngineComparison comparison,
        RobustnessSuiteArtifact robustness)
    {
        if (research.SchemaVersion != 1 || research.RunId == Guid.Empty || research.Strategies.Count == 0 ||
            !BacktestSpecificationCodec.Verify(specification) || !BacktestRunCodec.Verify(native) ||
            !CrossEngineComparisonCodec.Verify(comparison) || !RobustnessSuiteArtifactCodec.Verify(robustness))
            throw new InvalidDataException("Certificate V2 evidence is invalid or unverified.");
        if (native.EngineId != "native-csharp" || native.EngineRole != BacktestEngineRole.Authoritative ||
            native.SpecificationSha256 != specification.SpecificationSha256 ||
            native.DeclaredDatasetSha256 != specification.Specification.Data.DatasetSha256 ||
            research.Dataset.DatasetSha256 != specification.Specification.Data.DatasetSha256 ||
            comparison.SpecificationSha256 != specification.SpecificationSha256 ||
            comparison.NativeResultSha256 != native.ResultSha256 ||
            comparison.DatasetSha256 != native.DeclaredDatasetSha256 ||
            comparison.ConsumedMarketDataSha256 != native.ConsumedMarketDataSha256 ||
            robustness.SourceResultSha256 != native.ResultSha256 ||
            robustness.SpecificationSha256 != native.SpecificationSha256 ||
            robustness.DatasetSha256 != native.DeclaredDatasetSha256 ||
            robustness.ConsumedMarketDataSha256 != native.ConsumedMarketDataSha256)
            throw new InvalidDataException("Certificate V2 evidence does not describe one experiment.");
    }

    private static RobustnessScenarioResult AtLeast(IReadOnlyList<RobustnessScenarioResult> scenarios,
        decimal required, string unit, string name) => scenarios
        .Where(item => item.StressUnit == unit && item.StressValue >= required)
        .OrderBy(item => item.StressValue).FirstOrDefault() ??
        throw new InvalidDataException($"Robustness evidence lacks the required {name} scenario.");

    private static Command Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {args[index]}.");
            if (args[index] is not ("--research" or "--specification" or "--native" or "--comparison" or
                "--robustness" or "--output") || !values.TryAdd(args[index], args[index + 1]))
                throw new ArgumentException($"Unknown or duplicate option: {args[index]}");
        }
        return new(Input(values, "--research"), Input(values, "--specification"),
            Input(values, "--native"), Input(values, "--comparison"), Input(values, "--robustness"),
            Output(values, "--output"));
    }

    private static string Input(IReadOnlyDictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var value)) throw new ArgumentException($"{key} is required.");
        var path = JsonPath(value, key);
        if (!File.Exists(path)) throw new FileNotFoundException($"{key} file was not found.", path);
        return path;
    }

    private static string Output(IReadOnlyDictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var value)) throw new ArgumentException($"{key} is required.");
        var path = JsonPath(value, key);
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw new ArgumentException("Output directory does not exist.");
        if (File.Exists(path)) throw new IOException("Certificate V2 output already exists.");
        return path;
    }

    private static string JsonPath(string value, string option)
    {
        var path = Path.GetFullPath(value);
        if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{option} must use .json.");
        return path;
    }

    private static async Task<string> ReadTextAsync(string path, long maximum, CancellationToken token)
    {
        var length = new FileInfo(path).Length;
        if (length is < 2 || length > maximum) throw new InvalidDataException("Evidence file is empty or too large.");
        return await File.ReadAllTextAsync(path, token);
    }

    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Json) ??
        throw new InvalidDataException("Evidence JSON is empty.");
    private static string ParametersHash(IReadOnlyDictionary<string, decimal> parameters) => Sha256(
        JsonSerializer.Serialize(new SortedDictionary<string, decimal>(
            parameters.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
            StringComparer.Ordinal), Json));
    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static async Task WriteNewAtomicallyAsync(string path, string value, CancellationToken token)
    {
        var temporary = $"{path}.tmp-{Guid.NewGuid():N}";
        try { await File.WriteAllTextAsync(temporary, value, new UTF8Encoding(false), token); File.Move(temporary, path); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false));
        return options;
    }
    private sealed record Command(string Research, string Specification, string Native,
        string Comparison, string Robustness, string Output);
}
