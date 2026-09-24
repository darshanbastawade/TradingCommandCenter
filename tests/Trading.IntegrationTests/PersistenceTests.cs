using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Trading.Domain.MarketData;
using Trading.Infrastructure.Persistence;
using Trading.Domain.Research;
using Trading.Domain.Execution;

namespace Trading.IntegrationTests;

public sealed class PersistenceTests
{
    [Fact]
    public async Task Candles_roundtrip_with_UTC_precision_order_and_half_open_range()
    {
        await using var connection = await OpenAsync();
        await using var db = Create(connection);
        await db.Database.EnsureCreatedAsync();
        var store = new MarketDataStore(db);
        var instrument = Instrument();
        await store.AddInstrumentAsync(instrument);
        var start = DateTimeOffset.Parse("2026-09-14T09:15:00+05:30");
        await store.AddCandlesAsync([Bar(instrument.Id, start.AddMinutes(10)), Bar(instrument.Id, start), Bar(instrument.Id, start.AddMinutes(5))]);
        db.ChangeTracker.Clear();
        var candles = await store.ReadCandlesAsync(instrument.Id, Timeframe.Minute5, start, start.AddMinutes(10));
        Assert.Equal(2, candles.Count);
        Assert.Equal(start.UtcDateTime, candles[0].OpenTimeUtc);
        Assert.Equal(DateTimeKind.Utc, candles[0].OpenTimeUtc.Kind);
        Assert.Equal(100.1234m, candles[0].Open);
        Assert.Null(candles[0].OpenInterest);
        Assert.Single(await store.ReadCandlesAsync(instrument.Id, Timeframe.Minute5, start, start.AddMinutes(15), 1));
        Assert.Empty(await store.ReadCandlesAsync(Guid.NewGuid(), Timeframe.Minute5, start, start.AddMinutes(15)));
    }

    [Fact]
    public async Task Duplicate_candle_rolls_back_entire_batch()
    {
        await using var connection = await OpenAsync();
        await using var db = Create(connection);
        await db.Database.EnsureCreatedAsync();
        var store = new MarketDataStore(db);
        var instrument = Instrument();
        await store.AddInstrumentAsync(instrument);
        var start = DateTimeOffset.UtcNow;
        await store.AddCandlesAsync([Bar(instrument.Id, start)]);
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<DbUpdateException>(() => store.AddCandlesAsync([Bar(instrument.Id, start.AddMinutes(5)), Bar(instrument.Id, start)]));
        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.Candles.CountAsync());
    }

    [Fact]
    public async Task Database_enforces_foreign_key_unique_instrument_and_price_constraint()
    {
        await using var connection = await OpenAsync();
        await using var db = Create(connection);
        await db.Database.EnsureCreatedAsync();
        var store = new MarketDataStore(db);
        await Assert.ThrowsAsync<DbUpdateException>(() => store.AddCandlesAsync([Bar(Guid.NewGuid(), DateTimeOffset.UtcNow)]));
        db.ChangeTracker.Clear();
        var instrument = Instrument();
        await store.AddInstrumentAsync(instrument);
        await Assert.ThrowsAsync<DbUpdateException>(() => store.AddInstrumentAsync(Instrument()));
        db.ChangeTracker.Clear();
        await store.AddCandlesAsync([Bar(instrument.Id, DateTimeOffset.UtcNow)]);
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE Candles SET Volume = -1"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE Candles SET High = 1"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM Instruments"));
    }

    [Fact]
    public void SQL_Server_migration_matches_model_and_contains_constraints()
    {
        using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=SchemaOnly;Trusted_Connection=True").Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var sql = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("CREATE TABLE [Candles]", sql);
        Assert.Contains("decimal(18,4)", sql);
        Assert.Contains("datetime2(7)", sql);
        Assert.Contains("CK_Candles_Prices", sql);
        Assert.Contains("CREATE UNIQUE INDEX", sql);
        Assert.Contains("CREATE TABLE [ResearchRuns]", sql);
        Assert.Contains("CREATE TABLE [OptionContracts]", sql);
        Assert.Contains("CREATE TABLE [OptionQuotes]", sql);
        Assert.Contains("CREATE TABLE [StrategyCertificates]", sql);
        Assert.Contains("CREATE TABLE [BacktestAnalyses]", sql);
        Assert.Contains("CREATE TABLE [MarketFeedCaptures]", sql);
        Assert.Contains("CREATE TABLE [PaperTradingSessions]", sql);
        Assert.Contains("StrategyQualificationId", sql);
        Assert.Contains("CK_PaperTradingSessions_QualificationLineage", sql);
        Assert.Contains("CREATE TABLE [LiveOrders]", sql);
        Assert.Contains("CREATE TABLE [ControlledAutomationAuthorizations]", sql);
        Assert.Contains("CREATE TABLE [ReconciledExecutionStates]", sql);
        Assert.Contains("CREATE TABLE [InternalTradingLedgerSnapshots]", sql);
        Assert.Contains("CREATE TABLE [ParameterSweeps]", sql);
        Assert.Contains("CREATE TABLE [BacktestCandidates]", sql);
        Assert.Contains("CREATE TABLE [NativeCandidateVerificationRuns]", sql);
    }

    [Fact]
    public async Task Paper_sessions_require_certificate_and_capture_and_roundtrip()
    {
        await using var connection = await OpenAsync();
        await using var db = Create(connection);
        await db.Database.EnsureCreatedAsync();
        var now = DateTime.UtcNow; var instrument = Instrument();
        await db.Instruments.AddAsync(instrument);
        var run = new ResearchRun(Guid.NewGuid(), now, instrument.Id, Timeframe.Minute5,
            now.AddDays(-2), now.AddDays(-1), "fixture", "v1", "calendar", new string('a', 64),
            new string('b', 64), new string('c', 64), "revision", "{}");
        await db.ResearchRuns.AddAsync(run);
        var certificate = new IssuedStrategyCertificate(Guid.NewGuid(), run.Id, "strategy-v1", now,
            now.AddDays(30), "ResearchQualified", run.ArtifactSha256, new string('d', 64), "{}");
        var capture = new MarketFeedCapture(Guid.NewGuid(), now, "paperReplay", "full", 2,
            now.AddSeconds(-2), now.AddSeconds(-1), new string('e', 64), "{}");
        await db.StrategyCertificates.AddAsync(certificate); await db.MarketFeedCaptures.AddAsync(capture);
        await db.SaveChangesAsync();
        var store = new PaperTradingSessionStore(db);
        var session = new PaperTradingSession(Guid.NewGuid(), certificate.Id, capture.Id, now,
            "strategy-v1", 30_000, 30_100, 100, 1, 1, 0, new string('e', 64),
            new string('f', 64), "{}");
        await store.AddAsync(session); db.ChangeTracker.Clear();
        Assert.Equal(session.ArtifactSha256, (await store.FindAsync(session.Id))!.ArtifactSha256);
        Assert.Equal(session.Id, Assert.Single(await store.ListAsync()).Id);
        await Assert.ThrowsAsync<DbUpdateException>(() => store.AddAsync(new PaperTradingSession(Guid.NewGuid(),
            certificate.Id, capture.Id, now, "strategy-v1", 30_000, 30_000, 0, 1, 0, 1,
            new string('e', 64), new string('a', 64), "{}")));
        db.ChangeTracker.Clear();

        var qualificationId = Guid.NewGuid(); var otherQualificationId = Guid.NewGuid();
        var qualificationStarted = now.AddMinutes(1); var cutoff = now.AddMinutes(10);
        var qualifiedSessions = new[]
        {
            new PaperTradingSession(Guid.NewGuid(), certificate.Id, capture.Id, now.AddMinutes(2),
                "strategy-v1", 30_000, 30_100, 100, 1, 1, 0, new string('1', 64),
                new string('2', 64), "{}", qualificationId, new string('3', 64), Guid.NewGuid(),
                new string('4', 64), qualificationStarted),
            new PaperTradingSession(Guid.NewGuid(), certificate.Id, capture.Id, now.AddMinutes(3),
                "strategy-v1", 30_000, 29_500, -500, 1, 1, 0, new string('5', 64),
                new string('6', 64), "{}", qualificationId, new string('3', 64), Guid.NewGuid(),
                new string('7', 64), qualificationStarted),
            new PaperTradingSession(Guid.NewGuid(), certificate.Id, capture.Id, now.AddMinutes(4),
                "strategy-v1", 30_000, 31_000, 1_000, 1, 1, 0, new string('8', 64),
                new string('9', 64), "{}", otherQualificationId, new string('a', 64), Guid.NewGuid(),
                new string('b', 64), qualificationStarted)
        };
        foreach (var qualifiedSession in qualifiedSessions) await store.AddAsync(qualifiedSession);
        db.ChangeTracker.Clear();
        var discovered = await store.ListForQualificationAsync(qualificationId, qualificationStarted, cutoff);
        Assert.Equal(2, discovered.Count);
        Assert.Contains(discovered, item => item.RealizedNetPnl < 0);
        Assert.DoesNotContain(discovered, item => item.StrategyQualificationId == otherQualificationId);
        Assert.Throws<ArgumentException>(() => new PaperTradingSession(Guid.NewGuid(), certificate.Id, capture.Id,
            qualificationStarted.AddTicks(-1), "strategy-v1", 30_000, 30_100, 100, 1, 1, 0,
            new string('c', 64), new string('d', 64), "{}", qualificationId, new string('e', 64),
            Guid.NewGuid(), new string('f', 64), qualificationStarted));
    }

    [Fact]
    public async Task Live_orders_persist_prepared_before_submitted_and_reject_duplicate_request_ids()
    {
        await using var connection = await OpenAsync();
        await using var db = Create(connection);
        await db.Database.EnsureCreatedAsync();
        var now = DateTime.UtcNow; var instrument = Instrument();
        await db.Instruments.AddAsync(instrument);
        var run = new ResearchRun(Guid.NewGuid(), now, instrument.Id, Timeframe.Minute5,
            now.AddDays(-2), now.AddDays(-1), "fixture", "v1", "calendar", new string('a', 64),
            new string('b', 64), new string('c', 64), "revision", "{}");
        await db.ResearchRuns.AddAsync(run);
        var certificate = new IssuedStrategyCertificate(Guid.NewGuid(), run.Id, "strategy-v1", now,
            now.AddDays(30), "ResearchQualified", run.ArtifactSha256, new string('d', 64), "{}");
        await db.StrategyCertificates.AddAsync(certificate); await db.SaveChangesAsync();
        var store = new LiveOrderStore(db); var requestId = Guid.NewGuid();
        var order = new LiveOrderRecord(Guid.NewGuid(), certificate.Id, requestId, now, "DirectLive",
            "Prepared", "strategy-v1", "NFO", "TESTCE", 50, 100.2m, 90m, 120m,
            new string('e', 64), new string('f', 64), "", new string('1', 64), "{}");
        await store.AddAsync(order); db.ChangeTracker.Clear();

        var persisted = (await store.FindAsync(order.Id))!;
        Assert.Equal("Prepared", persisted.Status);
        persisted.MarkSubmitted("order-123", new string('2', 64), "{\"status\":\"Submitted\"}");
        await store.UpdateAsync(persisted); db.ChangeTracker.Clear();
        var submitted = (await store.FindAsync(order.Id))!;
        Assert.Equal("Submitted", submitted.Status);
        Assert.Equal("order-123", submitted.BrokerOrderId);
        Assert.Equal(order.Id, Assert.Single(await store.ListAsync()).Id);
        await Assert.ThrowsAsync<DbUpdateException>(() => store.AddAsync(new LiveOrderRecord(Guid.NewGuid(),
            certificate.Id, requestId, now, "SemiLive", "Proposed", "strategy-v1", "NFO", "TESTCE",
            50, 100.2m, 90m, 120m, new string('e', 64), new string('f', 64), "",
            new string('3', 64), "{}")));
    }

    [Fact]
    public async Task Market_feed_captures_are_immutable_and_roundtrip()
    {
        await using var connection = await OpenAsync();
        await using var db = Create(connection);
        await db.Database.EnsureCreatedAsync();
        var store = new MarketFeedCaptureStore(db);
        var created = new DateTime(2026, 9, 15, 4, 0, 0, DateTimeKind.Utc);
        var capture = new MarketFeedCapture(Guid.NewGuid(), created, "paperReplay", "full", 2,
            created, created.AddSeconds(1), new string('a', 64), "{}");
        await store.AddAsync(capture);
        db.ChangeTracker.Clear();
        Assert.Equal(capture.ArtifactSha256, (await store.FindAsync(capture.Id))!.ArtifactSha256);
        Assert.Equal(capture.Id, Assert.Single(await store.ListAsync()).Id);
        await Assert.ThrowsAsync<DbUpdateException>(() => store.AddAsync(new MarketFeedCapture(capture.Id,
            created, "paperReplay", "full", 1, created, created, new string('b', 64), "{}")));
    }

    [Fact]
    public async Task Strategy_certificates_are_immutable_and_unique_per_research_strategy()
    {
        await using var connection = await OpenAsync();
        await using var db = Create(connection);
        await db.Database.EnsureCreatedAsync();
        var instrument = Instrument();
        await db.Instruments.AddAsync(instrument);
        var hash = new string('a', 64);
        var run = new ResearchRun(Guid.NewGuid(), DateTimeOffset.UtcNow, instrument.Id, Timeframe.Minute5,
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, "fixture", "v1", "calendar",
            hash, new string('b', 64), new string('c', 64), "revision", "{\"schemaVersion\":1}");
        await db.ResearchRuns.AddAsync(run);
        await db.SaveChangesAsync();
        var store = new StrategyCertificateStore(db);
        var issued = DateTime.UtcNow;
        var certificate = new IssuedStrategyCertificate(Guid.NewGuid(), run.Id, "strategy-v1", issued,
            issued.AddDays(90), "ResearchQualified", run.ArtifactSha256, new string('d', 64), "{}");
        await store.AddRangeAsync([certificate]);
        db.ChangeTracker.Clear();

        Assert.Equal(certificate.CertificateSha256, (await store.FindAsync(certificate.Id))!.CertificateSha256);
        Assert.Equal(certificate.Id, Assert.Single(await store.ListAsync()).Id);
        await Assert.ThrowsAsync<DbUpdateException>(() => store.AddRangeAsync([
            new(Guid.NewGuid(), run.Id, "strategy-v1", issued, issued.AddDays(90),
                "ResearchQualified", run.ArtifactSha256, new string('e', 64), "{}") ]));
    }

    [Fact]
    public async Task Backtest_analyses_are_immutable_and_unique_per_run_prompt_and_deployment()
    {
        await using var connection = await OpenAsync();
        await using var db = Create(connection);
        await db.Database.EnsureCreatedAsync();
        var instrument = Instrument();
        await db.Instruments.AddAsync(instrument);
        var hash = new string('a', 64);
        var run = new ResearchRun(Guid.NewGuid(), DateTimeOffset.UtcNow, instrument.Id, Timeframe.Minute5,
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, "fixture", "v1", "calendar",
            hash, new string('b', 64), new string('c', 64), "revision", "{\"schemaVersion\":1}");
        await db.ResearchRuns.AddAsync(run);
        await db.SaveChangesAsync();
        var store = new BacktestAnalysisStore(db);
        var created = DateTime.UtcNow;
        var analysis = new BacktestAnalysis(Guid.NewGuid(), run.Id, created, "astra-deployment",
            "gpt-6-astra", "resp_1", "prompt-v1", new string('d', 64), run.ArtifactSha256,
            100, 20, new string('e', 64), "{}");
        await store.AddAsync(analysis);
        db.ChangeTracker.Clear();

        Assert.Equal(analysis.AnalysisSha256, (await store.FindAsync(analysis.Id))!.AnalysisSha256);
        Assert.NotNull(await store.FindExistingAsync(run.Id, analysis.PromptSha256, analysis.Deployment));
        Assert.Equal(analysis.Id, Assert.Single(await store.ListAsync()).Id);
        await Assert.ThrowsAsync<DbUpdateException>(() => store.AddAsync(new BacktestAnalysis(Guid.NewGuid(),
            run.Id, created, analysis.Deployment, "gpt-6-astra", "resp_2", "prompt-v1",
            analysis.PromptSha256, run.ArtifactSha256, 100, 20, new string('f', 64), "{}")));
    }

    [Fact]
    public async Task Option_contracts_and_quotes_roundtrip_with_relational_constraints()
    {
        await using var connection = await OpenAsync();
        await using var db = Create(connection);
        await db.Database.EnsureCreatedAsync();
        var underlying = Instrument();
        await db.Instruments.AddAsync(underlying);
        await db.SaveChangesAsync();
        var store = new OptionMarketDataStore(db);
        var contract = new OptionContract(Guid.NewGuid(), underlying.Id, "NFO", "SYNTHETIC26SEP100CE",
            new(2026, 9, 24), 100, OptionRight.Call, 50, .05m);
        await store.AddContractAsync(contract);
        var time = DateTimeOffset.Parse("2026-09-15T09:30:00+05:30");
        await store.AddQuotesAsync([new(contract.Id, time, 10, 10.5m, 10.25m, 100, 200)]);
        db.ChangeTracker.Clear();

        Assert.Single(await store.ReadContractsAsync(underlying.Id, new(2026, 9, 15), new(2026, 9, 30)));
        var quote = Assert.Single(await store.ReadQuotesAsync([contract.Id], time, time.AddMinutes(1)));
        Assert.Equal(time.UtcDateTime, quote.TimestampUtc);
        await Assert.ThrowsAsync<DbUpdateException>(() => store.AddQuotesAsync([
            new(contract.Id, time, 10, 10.5m, 10.25m, 100, 200)]));
    }

    [Fact]
    public async Task Research_runs_are_immutable_and_roundtrip_from_catalog()
    {
        await using var connection = await OpenAsync();
        await using var db = Create(connection);
        await db.Database.EnsureCreatedAsync();
        var instrument = Instrument();
        await db.Instruments.AddAsync(instrument);
        await db.SaveChangesAsync();
        var store = new ResearchRunStore(db);
        var hash = new string('a', 64);
        var run = new ResearchRun(Guid.NewGuid(), DateTimeOffset.UtcNow, instrument.Id, Timeframe.Minute5,
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, "fixture", "v1", "calendar",
            hash, new string('b', 64), new string('c', 64), "revision", "{\"schemaVersion\":1}");
        await store.AddAsync(run);
        db.ChangeTracker.Clear();

        Assert.Equal(run.ArtifactSha256, (await store.FindAsync(run.Id))!.ArtifactSha256);
        Assert.Equal(run.Id, Assert.Single(await store.ListAsync()).Id);
        await Assert.ThrowsAsync<DbUpdateException>(() => store.AddAsync(new ResearchRun(Guid.NewGuid(),
            DateTimeOffset.UtcNow, instrument.Id, Timeframe.Minute5, DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow, "fixture", "v1", "calendar", hash, new string('b', 64),
            new string('d', 64), "revision", "{}")));
    }

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        return connection;
    }
    private static TradingDbContext Create(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options);
    private static Instrument Instrument() => new(Guid.NewGuid(), "NSE", "SYNTHETIC", "Synthetic test data", 1, 0.05m);
    private static Candle Bar(Guid id, DateTimeOffset time) => new(id, Timeframe.Minute5, time, 100.1234m, 110, 90, 105, 0);
}
