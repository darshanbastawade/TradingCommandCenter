using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Trading.Application.Execution;
using Trading.Domain.Execution;
using Trading.Domain.MarketData;
using Trading.Domain.Research;
using Trading.Infrastructure.Persistence;

namespace Trading.IntegrationTests;

public sealed class ControlledAutomationAuthorizationStoreTests
{
    [Fact]
    public async Task Authorization_is_insert_only_and_cannot_be_consumed_twice()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync(); await using var db = Create(connection);
        var order = await SeedOrderAsync(db); var store = new ControlledAutomationAuthorizationStore(db);
        var reservation = Reservation(order.Id);
        Assert.True(await store.TryConsumeAsync(reservation, DateTime.UtcNow));
        Assert.False(await store.TryConsumeAsync(reservation, DateTime.UtcNow));
        Assert.True(await store.IsConsumedAsync(reservation.AutomationDecisionId));
        Assert.Equal(1, await db.ControlledAutomationAuthorizations.CountAsync());
    }

    [Fact]
    public async Task Concurrent_consumption_has_exactly_one_winner()
    {
        var file = Path.Combine(Path.GetTempPath(), $"m382-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={file};Foreign Keys=True;Default Timeout=10";
        try
        {
            Guid orderId;
            await using (var seed = Create(connectionString)) orderId = (await SeedOrderAsync(seed)).Id;
            var reservation = Reservation(orderId);
            await using var firstDb = Create(connectionString); await using var secondDb = Create(connectionString);
            var results = await Task.WhenAll(
                new ControlledAutomationAuthorizationStore(firstDb).TryConsumeAsync(reservation, DateTime.UtcNow),
                new ControlledAutomationAuthorizationStore(secondDb).TryConsumeAsync(reservation, DateTime.UtcNow));
            Assert.Single(results, value => value);
            await using var verify = Create(connectionString);
            Assert.Equal(1, await verify.ControlledAutomationAuthorizations.CountAsync());
        }
        finally { SqliteConnection.ClearAllPools(); if (File.Exists(file)) File.Delete(file); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Expired_or_invalid_hash_authorization_is_never_stored(bool expired)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync(); await using var db = Create(connection);
        var order = await SeedOrderAsync(db); var value = Reservation(order.Id);
        value = expired ? value with { ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1) } :
            value with { AutomationSha256 = "invalid" };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new ControlledAutomationAuthorizationStore(db).TryConsumeAsync(value, DateTime.UtcNow));
        Assert.Empty(db.ControlledAutomationAuthorizations);
    }

    [Fact]
    public async Task State_provider_uses_India_exchange_date_boundary()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync(); await using var db = Create(connection);
        await db.Database.EnsureCreatedAsync();
        var before = new DateTime(2026, 9, 23, 18, 29, 0, DateTimeKind.Utc);
        var after = new DateTime(2026, 9, 23, 18, 31, 0, DateTimeKind.Utc);
        db.ReconciledExecutionStates.AddRange(
            new(Guid.NewGuid(), before.AddSeconds(-1), new(2026, 9, 23), 0, 0, 0,
                Guid.NewGuid(), new string('a', 64), "fixture-23", true),
            new(Guid.NewGuid(), after.AddSeconds(-1), new(2026, 9, 24), 0, 0, 0,
                Guid.NewGuid(), new string('b', 64), "fixture-24", true));
        await db.SaveChangesAsync(); var provider = new ControlledAutomationStateProvider(db);
        Assert.Equal(new DateOnly(2026, 9, 23), (await provider.GetAsync(before)).ExchangeTradingDate);
        Assert.Equal(new DateOnly(2026, 9, 24), (await provider.GetAsync(after)).ExchangeTradingDate);
    }

    private static TradingDbContext Create(SqliteConnection connection) => new(
        new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options);
    private static TradingDbContext Create(string connection) => new(
        new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options);

    private static async Task<LiveOrderRecord> SeedOrderAsync(TradingDbContext db)
    {
        await db.Database.EnsureCreatedAsync(); var now = DateTime.UtcNow;
        var instrument = new Instrument(Guid.NewGuid(), "NSE", $"M{Guid.NewGuid():N}"[..10], "fixture", 25, .05m);
        var run = new ResearchRun(Guid.NewGuid(), now, instrument.Id, Timeframe.Minute5,
            now.AddDays(-2), now.AddDays(-1), "fixture", "v1", "calendar", new string('a', 64),
            new string('b', 64), new string('c', 64), "revision", "{}");
        var certificate = new IssuedStrategyCertificate(Guid.NewGuid(), run.Id, "strategy-v1", now,
            now.AddDays(1), "ResearchQualified", run.ArtifactSha256, new string('d', 64), "{}");
        var order = new LiveOrderRecord(Guid.NewGuid(), certificate.Id, Guid.NewGuid(), now, "DirectLive",
            "Prepared", "strategy-v1", "NFO", "TESTCE", 25, 100, 90, 120,
            new string('e', 64), new string('f', 64), "", new string('1', 64), "{}");
        db.AddRange(instrument, run, certificate, order); await db.SaveChangesAsync(); return order;
    }

    private static ControlledAutomationReservation Reservation(Guid orderId) => new(Guid.NewGuid(),
        new string('a', 64), Guid.NewGuid(), "action-1", "strategy-v1", DateTime.UtcNow.AddSeconds(-1),
        DateTime.UtcNow.AddSeconds(30), "DirectSubmissionEligible", 1, orderId);
}
