using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Trading.Api;
using Trading.Application.AI;
using Trading.Application.Research;
using Trading.Domain.MarketData;
using Trading.Domain.Research;
using Trading.Infrastructure.Persistence;

namespace Trading.IntegrationTests;

public sealed class BacktestAnalystCommandTests
{
    [Fact]
    public async Task Command_verifies_evidence_calls_analyst_once_and_persists_exact_artifact()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        var analyst = new StubAnalyst();
        await using var services = new ServiceCollection()
            .AddDbContext<TradingDbContext>(options => options.UseSqlite(connection))
            .AddScoped<IResearchRunStore, ResearchRunStore>()
            .AddScoped<IBacktestAnalysisStore, BacktestAnalysisStore>()
            .AddSingleton<IBacktestAnalyst>(analyst).BuildServiceProvider();
        var artifact = Artifact(); var artifactHash = Sha256(artifact);
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            await db.Database.EnsureCreatedAsync();
            await db.Instruments.AddAsync(new(InstrumentId, "NSE", "M20", "M20 fixture", 25, .05m));
            await db.ResearchRuns.AddAsync(new ResearchRun(RunId,
                new DateTimeOffset(2026, 9, 15, 10, 0, 0, TimeSpan.Zero), InstrumentId, Timeframe.Minute5,
                DateTimeOffset.Parse("2025-01-01T03:45:00Z"), DateTimeOffset.Parse("2026-01-01T03:45:00Z"),
                "fixture", "v1", "calendar-v1", DatasetHash, ConfigurationHash, artifactHash,
                "revision-v1", artifact));
            await db.SaveChangesAsync();
        }
        var destination = Path.Combine(Path.GetTempPath(), $"m20-{Guid.NewGuid():N}.json");
        try
        {
            var output = new StringWriter(); var error = new StringWriter();
            var args = new[] { "analyze-backtest", "--research-run-id", RunId.ToString(), "--output", destination };
            Assert.Equal(0, await BacktestAnalystCommands.RunAsync(args, services, output, error));
            Assert.Equal(1, analyst.CallCount);
            Assert.Equal(string.Empty, error.ToString());
            await using var scope = services.CreateAsyncScope();
            var summary = Assert.Single(await scope.ServiceProvider.GetRequiredService<IBacktestAnalysisStore>().ListAsync());
            var stored = await scope.ServiceProvider.GetRequiredService<IBacktestAnalysisStore>().FindAsync(summary.Id);
            Assert.Equal(await File.ReadAllTextAsync(destination), stored!.AnalysisJson);
            Assert.Contains("backtest-analysis-created", output.ToString());

            var secondError = new StringWriter();
            var secondArgs = args.ToArray();
            secondArgs[4] = destination + ".second.json";
            Assert.Equal(2, await BacktestAnalystCommands.RunAsync(secondArgs, services, TextWriter.Null, secondError));
            Assert.Contains("already has an analysis", secondError.ToString());
            Assert.Equal(1, analyst.CallCount);
        }
        finally
        {
            if (File.Exists(destination)) File.Delete(destination);
            if (File.Exists(destination + ".second.json")) File.Delete(destination + ".second.json");
        }
    }

    private static readonly Guid RunId = Guid.Parse("20202020-0000-0000-0000-000000000001");
    private static readonly Guid InstrumentId = Guid.Parse("20202020-0000-0000-0000-000000000002");
    private static readonly string DatasetHash = new('a', 64);
    private static readonly string ConfigurationHash = new('b', 64);

    private static string Artifact() => $$"""
    {
      "schemaVersion": 1, "runId": "{{RunId}}", "createdAtUtc": "2026-09-15T10:00:00Z",
      "sourceRevision": "revision-v1", "dataset": { "datasetSha256": "{{DatasetHash}}" },
      "configurationSha256": "{{ConfigurationHash}}", "configuration": {},
      "strategies": [{ "strategyId": "strategy-v1" }],
      "ranking": { "schemaVersion": 1, "rankings": [], "selectedStrategies": [] }
    }
    """;

    private static string Sha256(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class StubAnalyst : IBacktestAnalyst
    {
        public int CallCount { get; private set; }
        public string PromptVersion => "test-prompt-v1";
        public string PromptSha256 => new string('c', 64);
        public string Deployment => "astra-test-deployment";
        public Task<BacktestAnalystResponse> AnalyzeAsync(BacktestAnalystRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var result = new BacktestAnalystOutput(1, request.ResearchRunId, "Evidence summary",
                [new("strategy-v1", "Supplied evidence only", [], [], [], [], [], AnalystRecommendation.MoreResearch)],
                [], ["collect paper evidence"], ["no live authority"]);
            return Task.FromResult(new BacktestAnalystResponse("resp_fixture", "gpt-6-astra", 100, 25, result));
        }
    }
}
