using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Trading.Api;
using Trading.Application.Research;
using Trading.Domain.MarketData;
using Trading.Domain.Research;
using Trading.Infrastructure.Persistence;

namespace Trading.IntegrationTests;

public sealed class StrategyCertificateCommandTests
{
    [Fact]
    public async Task Command_verifies_research_issues_file_and_persists_identical_certificate()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["StrategyCertificates:ValidityDays"] = "90",
            ["StrategyCertificates:MaximumCertificatesPerRun"] = "2",
            ["StrategyCertificates:PolicyVersion"] = "strategy-certificate-v1"
        }).Build();
        await using var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration)
            .AddDbContext<TradingDbContext>(options => options.UseSqlite(connection))
            .AddScoped<IResearchRunStore, ResearchRunStore>()
            .AddScoped<IStrategyCertificateStore, StrategyCertificateStore>()
            .BuildServiceProvider();
        var runId = Guid.Parse("19191919-0000-0000-0000-000000000001");
        var instrumentId = Guid.Parse("19191919-0000-0000-0000-000000000002");
        var datasetHash = new string('a', 64);
        var configurationHash = new string('b', 64);
        var artifactJson = Artifact(runId, datasetHash, configurationHash);
        var artifactHash = Sha256(artifactJson);
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            await db.Database.EnsureCreatedAsync();
            await db.Instruments.AddAsync(new(instrumentId, "NSE", "M19", "M19 fixture", 25, .05m));
            await db.ResearchRuns.AddAsync(new ResearchRun(runId,
                new DateTimeOffset(2026, 9, 15, 10, 0, 0, TimeSpan.Zero), instrumentId, Timeframe.Minute5,
                DateTimeOffset.Parse("2025-01-01T03:45:00Z"), DateTimeOffset.Parse("2026-01-01T03:45:00Z"),
                "fixture", "v1", "calendar-v1", datasetHash, configurationHash, artifactHash,
                "revision-v1", artifactJson));
            await db.SaveChangesAsync();
        }
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"m19-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        try
        {
            var output = new StringWriter(); var error = new StringWriter();
            var exit = await StrategyCertificateCommands.RunAsync([
                "issue-certificates", "--research-run-id", runId.ToString(), "--output-dir", outputDirectory
            ], services, output, error);

            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, error.ToString());
            var file = Assert.Single(Directory.GetFiles(outputDirectory, "strategy-certificate-*.json"));
            await using var scope = services.CreateAsyncScope();
            var stored = Assert.Single(await scope.ServiceProvider.GetRequiredService<IStrategyCertificateStore>().ListAsync());
            var entity = await scope.ServiceProvider.GetRequiredService<IStrategyCertificateStore>().FindAsync(stored.Id);
            Assert.Equal(await File.ReadAllTextAsync(file), entity!.CertificateJson);
            Assert.Contains("strategy-certificates-issued", output.ToString());
            Assert.Contains("\"liveTradingAuthorized\": false", entity.CertificateJson);
        }
        finally
        {
            Directory.Delete(outputDirectory, true);
        }
    }

    private static string Artifact(Guid runId, string datasetHash, string configurationHash) => $$"""
    {
      "schemaVersion": 1,
      "runId": "{{runId}}",
      "createdAtUtc": "2026-09-15T10:00:00Z",
      "sourceRevision": "revision-v1",
      "dataset": { "datasetSha256": "{{datasetHash}}" },
      "configurationSha256": "{{configurationHash}}",
      "configuration": {
        "allowedRisk": 750, "maximumCapital": 100000, "maximumLots": 5,
        "slippageBasisPoints": 5, "costProfile": "options-costs-v1"
      },
      "strategies": [{ "strategyId": "qualified-strategy-v1" }],
      "ranking": {
        "schemaVersion": 1,
        "rankings": [{
          "rank": 1, "strategyId": "qualified-strategy-v1", "score": 91,
          "qualified": true, "qualificationFailures": [], "edgeScore": 18,
          "profitFactorScore": 14, "drawdownScore": 12, "walkForwardScore": 18,
          "robustnessScore": 14, "regimeScore": 10, "sampleSizeScore": 5
        }],
        "selectedStrategies": [{
          "rank": 1, "strategyId": "qualified-strategy-v1", "score": 91,
          "qualified": true, "qualificationFailures": [], "edgeScore": 18,
          "profitFactorScore": 14, "drawdownScore": 12, "walkForwardScore": 18,
          "robustnessScore": 14, "regimeScore": 10, "sampleSizeScore": 5
        }]
      }
    }
    """;

    private static string Sha256(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
