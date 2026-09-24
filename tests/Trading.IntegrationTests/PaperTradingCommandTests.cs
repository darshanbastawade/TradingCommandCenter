using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Trading.Api;
using Trading.Application.AI;
using Trading.Application.Execution;
using Trading.Application.MarketData;
using Trading.Application.Research;
using Trading.Backtesting.Certification;
using Trading.Backtesting.Ranking;
using Trading.Domain.MarketData;
using Trading.Domain.Research;
using Trading.Execution.Paper;
using Trading.Infrastructure.Persistence;

namespace Trading.IntegrationTests;

public sealed class PaperTradingCommandTests
{
    private static readonly JsonSerializerOptions Json = CreateJson();

    [Fact]
    public async Task Command_verifies_evidence_runs_risk_and_persists_exact_session()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        await using var services = new ServiceCollection()
            .AddDbContext<TradingDbContext>(options => options.UseSqlite(connection))
            .AddScoped<IStrategyCertificateStore, StrategyCertificateStore>()
            .AddScoped<IMarketFeedCaptureStore, MarketFeedCaptureStore>()
            .AddScoped<IPaperTradingSessionStore, PaperTradingSessionStore>().BuildServiceProvider();
        var now = DateTime.UtcNow; var entry = RecentWeekdayEntry();
        StrategyCertificate certificate;
        MarketFeedCaptureArtifact capture;
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            await db.Database.EnsureCreatedAsync();
            var instrumentId = Guid.NewGuid(); var researchId = Guid.NewGuid();
            await db.Instruments.AddAsync(new Instrument(instrumentId, "NSE", "PAPER", "Paper fixture", 25, .05m));
            var researchHash = new string('c', 64);
            await db.ResearchRuns.AddAsync(new ResearchRun(researchId, now.AddDays(-1), instrumentId,
                Timeframe.Minute5, now.AddDays(-365), now.AddDays(-2), "fixture", "v1", "calendar-v1",
                new string('a', 64), new string('b', 64), researchHash, "revision", "{}"));
            var score = new StrategyScore(1, "strategy-v1", 90, true, [], 18, 14, 12, 18, 14, 9, 5);
            certificate = Assert.Single(StrategyCertificateIssuer.Issue(new(researchId, now.AddDays(-1),
                instrumentId, 5, now.AddDays(-365), now.AddDays(-2), new string('a', 64),
                new string('b', 64), researchHash, "revision", new(750, 30_000, 2, 0, "fixture-costs-v1"),
                ["strategy-v1"], new(1, [score], [score]))));
            var certificateJson = JsonSerializer.Serialize(certificate, Json);
            await db.StrategyCertificates.AddAsync(new IssuedStrategyCertificate(certificate.CertificateId,
                researchId, certificate.StrategyId, certificate.IssuedAtUtc, certificate.ExpiresAtUtc,
                certificate.Status.ToString(), researchHash, certificate.CertificateSha256, certificateJson));
            var ticks = new[] { Tick(entry, 99, 100), Tick(entry.AddSeconds(1), 110, 111) };
            var unsignedCapture = new MarketFeedCaptureArtifact(1, Guid.NewGuid(), now,
                MarketFeedSource.PaperReplay, MarketFeedQuoteMode.Full,
                [new(12345, "NFO", "TESTCE", 100)], ticks, string.Empty);
            var captureHash = Sha256(JsonSerializer.Serialize(unsignedCapture, Json));
            capture = unsignedCapture with { ArtifactSha256 = captureHash };
            var captureJson = JsonSerializer.Serialize(capture, Json);
            await db.MarketFeedCaptures.AddAsync(new MarketFeedCapture(capture.CaptureId, now,
                "paperReplay", "full", ticks.Length, ticks[0].ReceivedAtUtc, ticks[^1].ReceivedAtUtc,
                captureHash, captureJson));
            await db.SaveChangesAsync();
        }
        var prefix = Path.Combine(Path.GetTempPath(), $"m22-{Guid.NewGuid():N}");
        var inputPath = prefix + "-input.json"; var outputPath = prefix + "-session.json";
        var qualificationPath = prefix + "-qualification.json";
        try
        {
            var input = new PaperTradingInput(30_000, 0, 1, 10, 5, false, true,
                [new(Guid.NewGuid(), certificate.InstrumentId, 12345, "NFO", "TESTCE", entry,
                    entry.AddSeconds(2), 90, 105, 25, 2)]);
            await File.WriteAllTextAsync(inputPath, JsonSerializer.Serialize(input, Json));
            var qualification = Qualification(certificate.ResearchRunId, certificate.StrategyId, now);
            await File.WriteAllTextAsync(qualificationPath, QualifiedStrategyPipeline.Serialize(qualification));
            var output = new StringWriter(); var error = new StringWriter();
            var result = await PaperTradingCommands.RunAsync(["paper-trade", "--certificate-id",
                certificate.CertificateId.ToString(), "--feed-capture-id", capture.CaptureId.ToString(),
                "--file", inputPath, "--output", outputPath, "--qualified-strategy", qualificationPath], services,
                new ConfigurationBuilder().Build(), output, error);
            Assert.Equal(0, result);
            Assert.Equal(string.Empty, error.ToString());
            await using var scope = services.CreateAsyncScope();
            var summary = Assert.Single(await scope.ServiceProvider.GetRequiredService<IPaperTradingSessionStore>().ListAsync());
            Assert.Equal(1, summary.FilledTrades);
            var stored = await scope.ServiceProvider.GetRequiredService<IPaperTradingSessionStore>().FindAsync(summary.Id);
            Assert.Equal(await File.ReadAllTextAsync(outputPath), stored!.ArtifactJson);
            Assert.Equal(qualification.QualificationId, stored.StrategyQualificationId);
            Assert.Equal(qualification.CertificateId, stored.QualificationCertificateId);
            using var artifact = JsonDocument.Parse(stored.ArtifactJson);
            Assert.Equal(2, artifact.RootElement.GetProperty("schemaVersion").GetInt32());
            var execution = artifact.RootElement.GetProperty("result");
            Assert.Equal(2, execution.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("requireBestBidAsk", execution.GetProperty("quoteQualityPolicy").GetString());
            var trade = execution.GetProperty("trades")[0];
            Assert.Equal("bestAsk", trade.GetProperty("entryPriceSource").GetString());
            Assert.Equal("bestBid", trade.GetProperty("exitPriceSource").GetString());
            Assert.Contains("paper-session-completed", output.ToString());
        }
        finally
        {
            if (File.Exists(inputPath)) File.Delete(inputPath);
            if (File.Exists(outputPath)) File.Delete(outputPath);
            if (File.Exists(qualificationPath)) File.Delete(qualificationPath);
        }
    }

    private static DateTime RecentWeekdayEntry()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));
        while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) date = date.AddDays(-1);
        var local = DateTime.SpecifyKind(date.ToDateTime(new TimeOnly(9, 15)), DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
    private static NormalizedMarketTick Tick(DateTime time, decimal bid, decimal last) =>
        new(MarketFeedSource.PaperReplay, MarketFeedQuoteMode.Full, 12345, "NFO", "TESTCE", time,
            time, last, 25, last, 1000, 500, 500, 95, 112, 90, 96, 1000, bid, bid + 1);
    private static string Sha256(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static QualifiedStrategyArtifact Qualification(Guid researchRunId, string strategyId, DateTime now)
    {
        const string policy = "qualified-strategy-pipeline-v1"; var certificateHash = new string('7', 64);
        var analysisHash = new string('8', 64); const string approval = "m38-paper-fixture";
        var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{policy}|{certificateHash}|{analysisHash}|{approval}"))[..16]);
        return QualifiedStrategyPipeline.Seal(new(1, id, now.AddMinutes(-1), now.AddDays(30), policy,
            Guid.NewGuid(), certificateHash, Guid.NewGuid(), analysisHash, researchRunId, strategyId, 1,
            ResearchAnalystV2Recommendation.QualificationReview, approval, true, false, true, false, false,
            string.Empty));
    }
    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
