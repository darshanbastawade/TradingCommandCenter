using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Trading.Application.Research;
using Trading.Backtesting.Certification;
using Trading.Backtesting.Reporting;
using Trading.Domain.Research;

namespace Trading.Api;

public static class StrategyCertificateCommands
{
    private static readonly JsonSerializerOptions Json = Options();

    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] == "issue-certificates";

    public static async Task<int> RunAsync(string[] args, IServiceProvider services, TextWriter output,
        TextWriter error, CancellationToken cancellationToken = default)
    {
        var created = new List<string>();
        try
        {
            var (runId, outputDirectory) = Parse(args);
            await using var scope = services.CreateAsyncScope();
            var run = await scope.ServiceProvider.GetRequiredService<IResearchRunStore>()
                .FindAsync(runId, cancellationToken) ?? throw new ArgumentException("The research run was not found.");
            var actualArtifactHash = Sha256(run.ArtifactJson);
            if (!string.Equals(actualArtifactHash, run.ArtifactSha256, StringComparison.Ordinal))
                throw new InvalidDataException("The stored research artifact does not match its SHA-256.");
            var artifact = JsonSerializer.Deserialize<ResearchIntegrityArtifact>(run.ArtifactJson, Json) ??
                throw new InvalidDataException("The stored research artifact is invalid.");
            if (artifact.RunId != run.Id || artifact.Dataset.DatasetSha256 != run.DatasetSha256 ||
                artifact.ConfigurationSha256 != run.ConfigurationSha256)
                throw new InvalidDataException("The stored research artifact identity does not match its catalog record.");

            var settings = services.GetRequiredService<IConfiguration>().GetSection("StrategyCertificates")
                .Get<StrategyCertificateSettings>() ?? new();
            var source = new StrategyCertificateSource(run.Id, run.CreatedAtUtc, run.InstrumentId,
                (int)run.Timeframe, run.FromUtc, run.ToUtc, run.DatasetSha256, run.ConfigurationSha256,
                run.ArtifactSha256, run.SourceRevision,
                new(artifact.Configuration.AllowedRisk, artifact.Configuration.MaximumCapital,
                    artifact.Configuration.MaximumLots, artifact.Configuration.SlippageBasisPoints,
                    artifact.Configuration.CostProfile),
                artifact.Strategies.Select(item => item.StrategyId).ToArray(), artifact.Ranking);
            var certificates = StrategyCertificateIssuer.Issue(source, settings);
            var entities = new List<IssuedStrategyCertificate>();
            foreach (var certificate in certificates)
            {
                var json = JsonSerializer.Serialize(certificate, Json);
                var path = Path.Combine(outputDirectory,
                    $"strategy-certificate-{certificate.StrategyId}-{certificate.CertificateId:N}.json");
                if (File.Exists(path)) throw new IOException($"Certificate output already exists: {Path.GetFileName(path)}");
                await WriteNewAtomicallyAsync(path, json, cancellationToken);
                created.Add(path);
                entities.Add(new(certificate.CertificateId, certificate.ResearchRunId, certificate.StrategyId,
                    certificate.IssuedAtUtc, certificate.ExpiresAtUtc, certificate.Status.ToString(),
                    certificate.ResearchArtifactSha256, certificate.CertificateSha256, json));
            }
            await scope.ServiceProvider.GetRequiredService<IStrategyCertificateStore>()
                .AddRangeAsync(entities, cancellationToken);
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                status = certificates.Count == 0 ? "no-qualified-strategies" : "strategy-certificates-issued",
                researchRunId = run.Id,
                count = certificates.Count,
                certificates = certificates.Select((item, index) => new
                {
                    item.CertificateId, item.StrategyId, item.ExpiresAtUtc,
                    item.CertificateSha256, output = created[index]
                })
            }, Json));
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Delete(created);
            await error.WriteLineAsync("Certificate issuance cancelled; no completed issuance should be assumed.");
            return 130;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or IOException or
                                           UnauthorizedAccessException)
        {
            Delete(created);
            await error.WriteLineAsync(exception.Message);
            return 2;
        }
        catch (Exception)
        {
            Delete(created);
            await error.WriteLineAsync("Certificate issuance failed. Check the M19 migration and research artifact.");
            return 3;
        }
    }

    private static (Guid RunId, string OutputDirectory) Parse(string[] args)
    {
        string? run = null; string? directory = null;
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {args[index]}.");
            switch (args[index])
            {
                case "--research-run-id" when run is null: run = args[index + 1]; break;
                case "--output-dir" when directory is null: directory = args[index + 1]; break;
                default: throw new ArgumentException($"Unknown or duplicate option: {args[index]}");
            }
        }
        if (!Guid.TryParse(run, out var runId) || runId == Guid.Empty)
            throw new ArgumentException("A valid --research-run-id is required.");
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("--output-dir is required.");
        var fullDirectory = ResolveDirectory(directory);
        if (!Directory.Exists(fullDirectory)) throw new ArgumentException("The --output-dir directory does not exist.");
        return (runId, fullDirectory);
    }

    private static string ResolveDirectory(string value)
    {
        if (Path.IsPathFullyQualified(value)) return Path.GetFullPath(value);
        var current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "TradingCommandCenter.sln")))
            current = current.Parent;
        return Path.GetFullPath(value, current?.FullName ?? Environment.CurrentDirectory);
    }

    private static async Task WriteNewAtomicallyAsync(string destination, string contents, CancellationToken token)
    {
        var temporary = $"{destination}.tmp-{Guid.NewGuid():N}";
        try { await File.WriteAllTextAsync(temporary, contents, new UTF8Encoding(false), token); File.Move(temporary, destination, false); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Delete(IEnumerable<string> paths)
    {
        foreach (var path in paths) if (File.Exists(path)) File.Delete(path);
    }

    private static string Sha256(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
