using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Trading.Api;
using Trading.Application.MarketData;
using Trading.Application.Research;
using Trading.Domain.MarketData;
using Trading.Infrastructure.Persistence;

namespace Trading.IntegrationTests;

public sealed class ResearchIntegrityCommandTests
{
    private static readonly Guid InstrumentId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    private static readonly DateOnly FirstSession = new(2026, 9, 7);
    private static readonly TimeZoneInfo India = TimeZoneInfo.CreateCustomTimeZone(
        "Research Command India", TimeSpan.FromMinutes(330), "Research Command India", "Research Command India");

    [Fact]
    public async Task Command_certifies_runs_all_strategies_and_persists_identical_artifact()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        await using var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddDbContext<TradingDbContext>(options => options.UseSqlite(connection))
            .AddScoped<IMarketDataStore, MarketDataStore>()
            .AddScoped<IResearchRunStore, ResearchRunStore>()
            .BuildServiceProvider();
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            await db.Database.EnsureCreatedAsync();
            var store = new MarketDataStore(db);
            await store.AddInstrumentAsync(new(InstrumentId, "NSE", "M16", "M16 fixture", 1, .05m));
            await store.AddCandlesAsync(Candles());
        }

        var destination = Path.Combine(Path.GetTempPath(), $"m16-{Guid.NewGuid():N}.json");
        var holidays = Path.Combine(Path.GetTempPath(), $"holidays-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(holidays, "# no holidays in fixture\n");
        try
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var exit = await ResearchIntegrityCommands.RunAsync(Args(destination, holidays), services, output, error);

            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, error.ToString());
            using var artifact = JsonDocument.Parse(await File.ReadAllTextAsync(destination));
            var dataset = artifact.RootElement.GetProperty("dataset");
            Assert.True(dataset.GetProperty("passed").GetBoolean());
            Assert.Equal(601, dataset.GetProperty("rawCandleCount").GetInt32());
            Assert.Equal(600, dataset.GetProperty("certifiedCandleCount").GetInt32());
            Assert.Equal(1, dataset.GetProperty("excludedCandleCount").GetInt32());
            Assert.Equal("outside-declared-session", dataset.GetProperty("exclusions")[0]
                .GetProperty("reason").GetString());
            Assert.Equal(5, artifact.RootElement.GetProperty("strategies").GetArrayLength());
            Assert.Equal(5, artifact.RootElement.GetProperty("ranking").GetProperty("rankings").GetArrayLength());
            await using var scope = services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IResearchRunStore>();
            var summary = Assert.Single(await store.ListAsync());
            var stored = Assert.IsType<Trading.Domain.Research.ResearchRun>(await store.FindAsync(summary.Id));
            Assert.Equal(artifact.RootElement.GetProperty("runId").GetGuid(), stored.Id);
            Assert.Equal(await File.ReadAllTextAsync(destination), stored.ArtifactJson);
            Assert.Contains("research-run-created", output.ToString());
        }
        finally
        {
            if (File.Exists(destination)) File.Delete(destination);
            if (File.Exists(holidays)) File.Delete(holidays);
        }
    }

    private static string[] Args(string destination, string holidayFile) =>
    [
        "run-research", "--instrument-id", InstrumentId.ToString(), "--timeframe", "5",
        "--from-session", "2026-09-07", "--to-session-exclusive", "2026-09-17",
        "--training-sessions", "2", "--testing-sessions", "2", "--embargo-sessions", "0",
        "--training-mode", "rolling", "--initial-capital", "100000", "--allowed-risk", "750",
        "--maximum-capital", "100000", "--slippage-bps", "5", "--cost-profile", "none",
        "--source-revision", "integration-fixture", "--data-source", "synthetic",
        "--data-version", "v1", "--calendar-id", "nse-fixture-v1", "--calendar-file", holidayFile,
        "--output", destination
    ];

    private static Candle[] Candles()
    {
        var sessions = new List<DateOnly>();
        for (var date = FirstSession; sessions.Count < 8; date = date.AddDays(1))
            if (date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday) sessions.Add(date);
        var certified = sessions.SelectMany((session, day) => Enumerable.Range(0, 75).Select(bar =>
        {
            var local = session.ToDateTime(new TimeOnly(9, 15).AddMinutes(bar * 5), DateTimeKind.Unspecified);
            var utc = TimeZoneInfo.ConvertTimeToUtc(local, India);
            var close = 100m + day + bar * .01m;
            return new Candle(InstrumentId, Timeframe.Minute5, new DateTimeOffset(utc, TimeSpan.Zero),
                close, close + 1, close - 1, close, 100);
        }));
        var postCloseLocal = FirstSession.ToDateTime(new TimeOnly(15, 35), DateTimeKind.Unspecified);
        var postCloseUtc = TimeZoneInfo.ConvertTimeToUtc(postCloseLocal, India);
        return certified.Append(new Candle(InstrumentId, Timeframe.Minute5,
            new DateTimeOffset(postCloseUtc, TimeSpan.Zero), 100, 101, 99, 100, 100)).ToArray();
    }
}
