using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trading.Application.Backtesting;

namespace Trading.Backtesting.Research;

public sealed record ParameterGridDefinition(int SchemaVersion, int TopCandidates,
    IReadOnlyDictionary<string, IReadOnlyList<decimal>> Parameters);

public sealed record ParameterSweepPlan(string GridSha256,
    IReadOnlyList<SealedBacktestSpecification> Candidates, int TopCandidates);

public static class ParameterSweepPlanner
{
    private const int MaximumCombinations = 10_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false) }
    };

    public static ParameterGridDefinition Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new JsonException("Parameter grid JSON is required.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false, MaxDepth = 16 });
        RejectDuplicates(document.RootElement);
        return JsonSerializer.Deserialize<ParameterGridDefinition>(json, Json) ??
            throw new JsonException("Parameter grid is required.");
    }

    public static ParameterSweepPlan Create(SealedBacktestSpecification baseline, ParameterGridDefinition grid)
    {
        if (!BacktestSpecificationCodec.Verify(baseline))
            throw new ArgumentException("Baseline specification hash is invalid.", nameof(baseline));
        if (grid.SchemaVersion != 1 || grid.TopCandidates is < 1 or > 100 || grid.Parameters.Count == 0)
            throw new ArgumentException("Parameter grid identity or top-candidate count is invalid.", nameof(grid));
        var normalized = new SortedDictionary<string, decimal[]>(StringComparer.Ordinal);
        long combinations = 1;
        foreach (var item in grid.Parameters.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (!baseline.Specification.Parameters.ContainsKey(item.Key) || item.Value.Count is < 1 or > 100)
                throw new ArgumentException($"Grid parameter '{item.Key}' is unknown or empty.", nameof(grid));
            var values = item.Value.Distinct().Order().ToArray();
            if (values.Length != item.Value.Count)
                throw new ArgumentException($"Grid parameter '{item.Key}' contains duplicate values.", nameof(grid));
            combinations = checked(combinations * values.Length);
            if (combinations > MaximumCombinations)
                throw new ArgumentException($"Parameter grid exceeds {MaximumCombinations} combinations.", nameof(grid));
            normalized[item.Key] = values;
        }
        if (grid.TopCandidates > combinations)
            throw new ArgumentException("TopCandidates cannot exceed the number of combinations.", nameof(grid));
        var baselineParameters = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var parameter in baseline.Specification.Parameters) baselineParameters.Add(parameter.Key, parameter.Value);
        var parameterSets = new List<IReadOnlyDictionary<string, decimal>> { baselineParameters };
        foreach (var dimension in normalized)
            parameterSets = parameterSets.SelectMany(current => dimension.Value.Select(value =>
            {
                var copy = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
                foreach (var parameter in current) copy.Add(parameter.Key, parameter.Value);
                copy[dimension.Key] = value;
                return (IReadOnlyDictionary<string, decimal>)copy;
            })).ToList();
        var candidates = parameterSets.Select(parameters => BacktestSpecificationCodec.Seal(
            baseline.Specification with { Parameters = parameters })).ToArray();
        var canonicalGrid = JsonSerializer.Serialize(new ParameterGridDefinition(1, grid.TopCandidates,
            normalized.ToDictionary(item => item.Key,
                item => (IReadOnlyList<decimal>)item.Value, StringComparer.Ordinal)), Json);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalGrid))).ToLowerInvariant();
        return new(hash, candidates, grid.TopCandidates);
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException($"Duplicate property '{property.Name}'.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }
}
