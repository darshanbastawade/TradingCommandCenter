using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trading.Application.AI;
using Trading.Application.Research;
using Trading.Backtesting.Reporting;
using Trading.Domain.Research;

namespace Trading.Api;

public static class BacktestAnalystCommands
{
    private static readonly JsonSerializerOptions Json = Options();

    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] == "analyze-backtest";

    public static async Task<int> RunAsync(string[] args, IServiceProvider services, TextWriter output,
        TextWriter error, CancellationToken cancellationToken = default)
    {
        string? createdFile = null;
        try
        {
            var (runId, destination) = Parse(args);
            await using var scope = services.CreateAsyncScope();
            var research = await scope.ServiceProvider.GetRequiredService<IResearchRunStore>()
                .FindAsync(runId, cancellationToken) ?? throw new ArgumentException("The research run was not found.");
            if (Sha256(research.ArtifactJson) != research.ArtifactSha256)
                throw new InvalidDataException("The stored research artifact does not match its SHA-256.");
            var artifact = JsonSerializer.Deserialize<ResearchIntegrityArtifact>(research.ArtifactJson, Json) ??
                throw new InvalidDataException("The stored research artifact is invalid.");
            if (artifact.RunId != research.Id || artifact.Dataset.DatasetSha256 != research.DatasetSha256 ||
                artifact.ConfigurationSha256 != research.ConfigurationSha256 || artifact.Strategies.Count == 0)
                throw new InvalidDataException("The stored research artifact identity or strategy evidence is invalid.");

            var analyst = scope.ServiceProvider.GetRequiredService<IBacktestAnalyst>();
            var store = scope.ServiceProvider.GetRequiredService<IBacktestAnalysisStore>();
            if (await store.FindExistingAsync(runId, analyst.PromptSha256, analyst.Deployment, cancellationToken) is not null)
                throw new InvalidOperationException("This research run already has an analysis for the configured deployment and prompt.");
            var response = await analyst.AnalyzeAsync(new(runId, research.ArtifactSha256, research.ArtifactJson,
                artifact.Strategies.Select(item => item.StrategyId).ToArray()), cancellationToken);
            var id = Guid.NewGuid(); var createdAt = DateTime.UtcNow;
            var unsigned = new BacktestAnalysisArtifact(1, id, createdAt, runId, research.ArtifactSha256,
                analyst.Deployment, response.ResponseModel, response.ProviderResponseId, analyst.PromptVersion,
                analyst.PromptSha256, response.InputTokens, response.OutputTokens, response.Output, string.Empty);
            var hash = Sha256(JsonSerializer.Serialize(unsigned, Json));
            var completed = unsigned with { AnalysisSha256 = hash };
            var json = JsonSerializer.Serialize(completed, Json);
            await WriteNewAtomicallyAsync(destination, json, cancellationToken);
            createdFile = destination;
            await store.AddAsync(new BacktestAnalysis(id, runId, createdAt, analyst.Deployment,
                response.ResponseModel, response.ProviderResponseId, analyst.PromptVersion, analyst.PromptSha256,
                research.ArtifactSha256, response.InputTokens, response.OutputTokens, hash, json), cancellationToken);
            await output.WriteLineAsync(JsonSerializer.Serialize(new { status = "backtest-analysis-created",
                analysisId = id, researchRunId = runId, output = destination,
                analysisSha256 = hash, response.InputTokens, response.OutputTokens }, Json));
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Delete(createdFile); await error.WriteLineAsync("Backtest analysis cancelled; no completed analysis should be assumed."); return 130;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or IOException or
                                           UnauthorizedAccessException or InvalidOperationException)
        {
            Delete(createdFile); await error.WriteLineAsync(exception.Message); return 2;
        }
        catch (HttpRequestException exception)
        {
            Delete(createdFile); await error.WriteLineAsync(exception.Message); return 3;
        }
        catch (Exception)
        {
            Delete(createdFile); await error.WriteLineAsync("Backtest analysis failed. No analysis was persisted."); return 3;
        }
    }

    private static (Guid RunId, string Destination) Parse(string[] args)
    {
        string? run = null; string? output = null;
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {args[index]}.");
            switch (args[index])
            {
                case "--research-run-id" when run is null: run = args[index + 1]; break;
                case "--output" when output is null: output = args[index + 1]; break;
                default: throw new ArgumentException($"Unknown or duplicate option: {args[index]}");
            }
        }
        if (!Guid.TryParse(run, out var id) || id == Guid.Empty) throw new ArgumentException("A valid --research-run-id is required.");
        if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("--output is required.");
        var destination = ResolvePath(output);
        if (!string.Equals(Path.GetExtension(destination), ".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("--output must use the .json extension.");
        if (!Directory.Exists(Path.GetDirectoryName(destination))) throw new ArgumentException("The --output directory does not exist.");
        if (File.Exists(destination)) throw new IOException("The output file already exists.");
        return (id, destination);
    }

    private static string ResolvePath(string value)
    {
        if (Path.IsPathFullyQualified(value)) return Path.GetFullPath(value);
        var current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "TradingCommandCenter.sln"))) current = current.Parent;
        return Path.GetFullPath(value, current?.FullName ?? Environment.CurrentDirectory);
    }

    private static async Task WriteNewAtomicallyAsync(string destination, string contents, CancellationToken token)
    {
        var temporary = $"{destination}.tmp-{Guid.NewGuid():N}";
        try { await File.WriteAllTextAsync(temporary, contents, new UTF8Encoding(false), token); File.Move(temporary, destination, false); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Delete(string? path) { if (path is not null && File.Exists(path)) File.Delete(path); }
    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
