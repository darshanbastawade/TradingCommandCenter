using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Trading.Api;
using Trading.Application.AI;
using Trading.Application.Execution;
using Trading.Backtesting.Certification;
using Trading.Domain.Execution;
using Trading.Execution.Paper;
using Trading.Execution.Qualification;

namespace Trading.IntegrationTests;

public sealed class PaperQualificationCommandTests
{
    private static readonly JsonSerializerOptions Json = Options();

    [Fact]
    public async Task Production_command_uses_all_store_sessions_rejects_tampering_and_is_deterministic()
    {
        var now = DateTime.UtcNow; var start = now.AddHours(-2); var cutoff = now.AddMinutes(-1);
        var qualified = Qualification(start, now.AddDays(30));
        var sessions = Enumerable.Range(0, 10).Select(index => Session(qualified,
            start.AddMinutes(index + 1), index == 9 ? -200m : 100m)).ToList();
        var altered = Session(qualified, start.AddMinutes(20), 1_000m);
        altered = new PaperTradingSession(altered.Id, altered.StrategyCertificateId,
            altered.MarketFeedCaptureId, altered.CreatedAtUtc, altered.StrategyId, altered.InitialCash,
            altered.EndingCash, altered.RealizedNetPnl, altered.SubmittedOrders, altered.FilledTrades,
            altered.RejectedOrders, altered.ConfigurationSha256, altered.ArtifactSha256,
            altered.ArtifactJson.Replace("strategy-v1", "strategy-vX", StringComparison.Ordinal),
            altered.StrategyQualificationId, altered.StrategyQualificationSha256,
            altered.QualificationCertificateId, altered.QualificationCertificateSha256,
            altered.QualificationStartedAtUtc);
        sessions.Add(altered);
        await using var services = new ServiceCollection()
            .AddSingleton<IPaperQualificationSessionQuery>(new Query(sessions)).BuildServiceProvider();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PaperQualification:MinimumSessions"] = "10",
            ["PaperQualification:MinimumDistinctTradingDays"] = "1",
            ["PaperQualification:MinimumFilledTrades"] = "10",
            ["PaperQualification:MinimumProfitableSessionRate"] = "0.5",
            ["PaperQualification:MaximumRejectedOrderRate"] = "0.25",
            ["PaperQualification:RequirePositiveAggregateNetPnl"] = "true"
        }).Build();
        var prefix = Path.Combine(Path.GetTempPath(), $"m384-{Guid.NewGuid():N}");
        var qualifiedPath = prefix + "-qualified.json"; var inputPath = prefix + "-input.json";
        var firstPath = prefix + "-first.json"; var secondPath = prefix + "-second.json";
        try
        {
            await File.WriteAllTextAsync(qualifiedPath, QualifiedStrategyPipeline.Serialize(qualified));
            await File.WriteAllTextAsync(inputPath, JsonSerializer.Serialize(
                new PaperQualificationFileInput(cutoff), Json));
            var args = new[] { "qualify-paper", "--qualified-strategy", qualifiedPath,
                "--file", inputPath, "--output", firstPath };
            Assert.Equal(4, await PaperQualificationCommands.RunAsync(args, services, configuration,
                TextWriter.Null, TextWriter.Null));
            args[^1] = secondPath;
            Assert.Equal(4, await PaperQualificationCommands.RunAsync(args, services, configuration,
                TextWriter.Null, TextWriter.Null));
            var first = JsonSerializer.Deserialize<PaperQualificationArtifact>(
                await File.ReadAllTextAsync(firstPath), Json)!;
            var second = JsonSerializer.Deserialize<PaperQualificationArtifact>(
                await File.ReadAllTextAsync(secondPath), Json)!;
            Assert.True(PaperQualificationEngine.Verify(first));
            Assert.Equal(11, first.DiscoveredSessionCount);
            Assert.Equal(10, first.AcceptedSessionCount);
            Assert.Equal(700m, first.AggregateNetPnl);
            Assert.Equal(altered.Id, Assert.Single(first.RejectedSessions!).SessionId);
            Assert.Equal(10, first.IncludedSessions!.Count);
            Assert.Equal(first.PaperQualificationSha256, second.PaperQualificationSha256);
        }
        finally
        {
            foreach (var path in new[] { qualifiedPath, inputPath, firstPath, secondPath })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    private static QualifiedStrategyArtifact Qualification(DateTime start, DateTime expires)
    {
        const string policy = "qualified-strategy-pipeline-v1"; const string approval = "m38.4-fixture";
        var certificateHash = new string('a', 64); var analysisHash = new string('b', 64);
        var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{policy}|{certificateHash}|{analysisHash}|{approval}"))[..16]);
        return QualifiedStrategyPipeline.Seal(new(1, id, start, expires, policy, Guid.NewGuid(),
            certificateHash, Guid.NewGuid(), analysisHash, Guid.NewGuid(), "strategy-v1", 1,
            ResearchAnalystV2Recommendation.QualificationReview, approval, true, false, true, false, false,
            string.Empty));
    }

    private static PaperTradingSession Session(QualifiedStrategyArtifact qualification, DateTime createdAt,
        decimal netPnl)
    {
        var sessionId = Guid.NewGuid(); var legacyCertificateId = Guid.NewGuid(); var captureId = Guid.NewGuid();
        var trade = new PaperTradeResult(Guid.NewGuid(), PaperOrderStatus.FilledAndClosed, null, 123,
            "NFO", "TESTCE", createdAt, createdAt.AddMinutes(5), 25, 1, createdAt, 100,
            createdAt.AddMinutes(1), 100 + netPnl / 25, PaperExitReason.PlannedExit, 250,
            netPnl, 0, netPnl, new string('c', 64), []);
        var result = new PaperTradingResult(1, sessionId, createdAt, qualification.StrategyId,
            30_000, 30_000 + netPnl, netPnl, 0, netPnl, 1, 1, 0, [trade]);
        var unsigned = new QualifiedPaperTradingSessionArtifact(2, sessionId, createdAt,
            legacyCertificateId, new string('d', 64), captureId, new string('e', 64), "risk-v1",
            "costs-v1", new string('f', 64), true, result, qualification.QualificationId,
            qualification.QualificationSha256, qualification.CertificateId, qualification.CertificateSha256,
            qualification.QualifiedAtUtc, string.Empty);
        var hash = Sha256(JsonSerializer.Serialize(unsigned, Json));
        var artifactJson = JsonSerializer.Serialize(unsigned with { ArtifactSha256 = hash }, Json);
        return new(sessionId, legacyCertificateId, captureId, createdAt, qualification.StrategyId,
            result.InitialCash, result.EndingCash, result.RealizedNetPnl, result.SubmittedOrders,
            result.FilledTrades, result.RejectedOrders, unsigned.ConfigurationSha256, hash, artifactJson,
            qualification.QualificationId, qualification.QualificationSha256, qualification.CertificateId,
            qualification.CertificateSha256, qualification.QualifiedAtUtc);
    }

    private static string Sha256(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false));
        return options;
    }

    private sealed class Query(IReadOnlyList<PaperTradingSession> sessions) : IPaperQualificationSessionQuery
    {
        public Task<IReadOnlyList<PaperTradingSession>> ListForQualificationAsync(Guid qualificationId,
            DateTime observationStartUtc, DateTime observationCutoffUtc,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PaperTradingSession>>(
            sessions.Where(item => item.StrategyQualificationId == qualificationId &&
                item.CreatedAtUtc >= observationStartUtc && item.CreatedAtUtc <= observationCutoffUtc)
                .OrderBy(item => item.CreatedAtUtc).ThenBy(item => item.Id).ToArray());
    }
}
