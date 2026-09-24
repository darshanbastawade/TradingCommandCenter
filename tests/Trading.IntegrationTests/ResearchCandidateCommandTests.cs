using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Trading.Api;
using Trading.Application.Backtesting;
using Trading.Application.MarketData;
using Trading.Application.Research;
using Trading.Domain.MarketData;
using Trading.Domain.Research;
using Trading.Infrastructure.Persistence;

namespace Trading.IntegrationTests;

public sealed class ResearchCandidateCommandTests
{
    [Fact]
    public async Task Sweep_persists_ranked_candidate_then_native_verification_preserves_run()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        var services = new ServiceCollection()
            .AddDbContext<TradingDbContext>(options => options.UseSqlite(connection))
            .AddScoped<IMarketDataStore, MarketDataStore>()
            .AddScoped<IBacktestCandidateStore, BacktestCandidateStore>()
            .AddSingleton<IResearchBacktestWorker, FakeWorker>()
            .AddSingleton<IResearchBacktestWorker, LegacyFakeWorker>()
            .AddSingleton<IBacktestEngine, FakeEngine>()
            .BuildServiceProvider();
        var instrumentId = Guid.NewGuid(); var from = new DateTime(2026, 1, 1, 3, 45, 0, DateTimeKind.Utc);
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>(); await db.Database.EnsureCreatedAsync();
            db.Instruments.Add(new(instrumentId, "NSE", "TEST", "Fixture", 1, .05m));
            db.Candles.AddRange(new Candle(instrumentId, Timeframe.Minute5, from, 100, 101, 99, 100, 10),
                new Candle(instrumentId, Timeframe.Minute5, from.AddMinutes(5), 100, 102, 99, 101, 20));
            await db.SaveChangesAsync();
        }
        var specification = BacktestSpecificationCodec.Seal(new(1, "fixture-strategy-v1",
            new(instrumentId, "NSE", "TEST", BacktestAssetClass.Equity, "INR", 1, .05m),
            new(from, from.AddMinutes(10), 5, "India Standard Time", "nse-v1", "fixture", "v1",
                new string('a', 64), BacktestMarketDataMode.OhlcvBars), new(100_000, 750, 30_000, 5),
            new(3, 0, "none", new TimeOnly(15, 25), BacktestSignalTiming.CompletedBar,
                BacktestEntryFillPolicy.NextObservedBarOpen, BacktestAmbiguousBarPolicy.StopFirst,
                BacktestEndOfDataPolicy.CloseLastObserved),
            new Dictionary<string, decimal> { ["period"] = 20 }));
        var root = Path.Combine(Path.GetTempPath(), $"m28-{Guid.NewGuid():N}"); Directory.CreateDirectory(root);
        var specPath = Path.Combine(root, "spec.json"); var gridPath = Path.Combine(root, "grid.json");
        var sweepPath = Path.Combine(root, "sweep.json"); var verifyPath = Path.Combine(root, "verify.json");
        var rejectedPath = Path.Combine(root, "legacy-sweep.json");
        await File.WriteAllTextAsync(specPath, BacktestSpecificationCodec.Serialize(specification));
        await File.WriteAllTextAsync(gridPath,
            "{\"schemaVersion\":1,\"topCandidates\":1,\"parameters\":{\"period\":[10,20]}}");
        try
        {
            var rejectedExit = await ResearchCandidateCommands.RunAsync(["sweep-parameters", "--file", specPath,
                "--grid", gridPath, "--worker", "legacy-vectorbt", "--output", rejectedPath], services,
                TextWriter.Null, TextWriter.Null);
            Assert.Equal(2, rejectedExit);
            Assert.False(File.Exists(rejectedPath));
            await using (var rejectedScope = services.CreateAsyncScope())
                Assert.Empty(await rejectedScope.ServiceProvider.GetRequiredService<IBacktestCandidateStore>()
                    .ListSweepsAsync());

            var sweepExit = await ResearchCandidateCommands.RunAsync(["sweep-parameters", "--file", specPath,
                "--grid", gridPath, "--worker", "vectorbt", "--output", sweepPath], services,
                TextWriter.Null, TextWriter.Null);
            Assert.Equal(0, sweepExit);
            Guid sweepId;
            await using (var scope = services.CreateAsyncScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<IBacktestCandidateStore>();
                var sweep = Assert.Single(await store.ListSweepsAsync()); sweepId = sweep.Id;
                Assert.Equal(2, sweep.EvaluatedCandidates); Assert.Equal(1, sweep.StoredCandidates);
                Assert.Equal(BacktestCandidateStatus.ResearchProposed,
                    Assert.Single(await store.ListCandidatesAsync(sweepId)).Status);
            }
            var verifyExit = await ResearchCandidateCommands.RunAsync(["verify-backtest-candidates",
                "--sweep-id", sweepId.ToString(), "--top", "1", "--output", verifyPath], services,
                TextWriter.Null, TextWriter.Null);
            Assert.Equal(0, verifyExit);
            await using (var scope = services.CreateAsyncScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<IBacktestCandidateStore>();
                var sweep = (await store.FindSweepAsync(sweepId))!;
                var candidate = Assert.Single(await store.ListCandidatesAsync(sweepId));
                Assert.True(sweep.NativeVerificationCompleted);
                Assert.Equal(BacktestCandidateStatus.NativeVerified, candidate.Status);
                Assert.Contains("resultSha256", candidate.NativeRunJson);
            }
        }
        finally { Directory.Delete(root, true); await services.DisposeAsync(); }
    }

    private sealed class LegacyFakeWorker : IResearchBacktestWorker
    {
        public string WorkerId => "legacy-vectorbt"; public string WorkerVersion => "1.1.0";
        public BacktestEngineRole Role => BacktestEngineRole.ResearchExploration;
        public Task<ResearchWorkerResult> RunAsync(ResearchWorkerRequest request,
            CancellationToken cancellationToken = default)
        {
            var candidate = request.Candidates[0];
            return Task.FromResult(ResearchWorkerEvidenceCodec.Seal(new(1, WorkerId, WorkerVersion, Role,
                request.RequestId, request.RequestSha256,
                [new(candidate.CandidateKey, new(10, 5, 2, 3, 66.67m, 1.1m))], string.Empty), request));
        }
    }

    private sealed class FakeWorker : IResearchBacktestWorker
    {
        public string WorkerId => "vectorbt"; public string WorkerVersion => "1.1.0";
        public BacktestEngineRole Role => BacktestEngineRole.ResearchExploration;
        public Task<ResearchWorkerResult> RunAsync(ResearchWorkerRequest request,
            CancellationToken cancellationToken = default)
        {
            var candidate = request.Candidates.OrderBy(item => item.CandidateKey, StringComparer.Ordinal).First();
            return Task.FromResult(ResearchWorkerEvidenceCodec.Seal(new ResearchWorkerResult(2, WorkerId, WorkerVersion, Role,
                request.RequestId, request.RequestSha256,
                [new(candidate.CandidateKey, new(10, 5, 2, 3, 66.67m, 1.1m))], string.Empty)
                { ParityEvidence = new(request.StrategyId, true, "fixture-v1", new string('f', 64)) }, request));
        }
    }

    private sealed class FakeEngine : IBacktestEngine
    {
        public string EngineId => "native-fixture"; public string EngineVersion => "1";
        public BacktestEngineRole Role => BacktestEngineRole.Authoritative;
        public Task<BacktestRun> RunAsync(SealedBacktestSpecification specification,
            CancellationToken cancellationToken = default) => Task.FromResult(BacktestRunCodec.Seal(new(1,
                EngineId, EngineVersion, Role, specification.SpecificationSha256,
                specification.Specification.Data.DatasetSha256, new string('b', 64), 100_000, 100_000,
                0, 0, 0, [], [], string.Empty)));
    }
}
