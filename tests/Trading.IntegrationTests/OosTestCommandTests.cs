using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Trading.Api;
using Trading.Application.MarketData;
using Trading.Domain.MarketData;
using Trading.Infrastructure.Persistence;

namespace Trading.IntegrationTests;

public sealed class OosTestCommandTests
{
    private static readonly Guid InstrumentId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T09:15:00+05:30");

    [Fact]
    public async Task Cli_loads_sql_candles_and_creates_a_new_oos_json_artifact()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        await using var services = new ServiceCollection()
            .AddDbContext<TradingDbContext>(options => options.UseSqlite(connection))
            .AddScoped<IMarketDataStore, MarketDataStore>().BuildServiceProvider();
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            await db.Database.EnsureCreatedAsync();
            var store = new MarketDataStore(db);
            await store.AddInstrumentAsync(new(InstrumentId, "NSE", "SYNTHETIC", "Synthetic OOS", 50, .05m));
            await store.AddCandlesAsync(Candles());
        }

        var destination = Path.Combine(Path.GetTempPath(), $"oos-{Guid.NewGuid():N}.json");
        try
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var exit = await OosTestCommands.RunAsync(Args(destination), services, output, error);

            Assert.Equal(0, exit);
            Assert.True(File.Exists(destination));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(destination));
            var root = document.RootElement;
            Assert.Equal("chronological-holdout", root.GetProperty("testType").GetString());
            Assert.Equal(120, root.GetProperty("sourceCandleCount").GetInt32());
            Assert.Equal(1, root.GetProperty("window").GetProperty("testingSessionCount").GetInt32());
            Assert.Equal(0, root.GetProperty("report").GetProperty("summary").GetProperty("totalTrades").GetInt32());
            Assert.Contains("oos-report-created", output.ToString());
            Assert.Equal(string.Empty, error.ToString());

            Assert.Equal(2, await OosTestCommands.RunAsync(Args(destination), services, output, error));
        }
        finally
        {
            if (File.Exists(destination)) File.Delete(destination);
        }
    }

    [Fact]
    public async Task Cli_rejects_implicit_timezones_and_missing_required_risk_inputs_before_database_access()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        var output = new StringWriter();
        var error = new StringWriter();
        var args = Args(Path.Combine(Path.GetTempPath(), $"oos-{Guid.NewGuid():N}.json"));
        args[Array.IndexOf(args, "2026-09-01T00:00:00+05:30")] = "2026-09-01T00:00:00";
        Assert.Equal(2, await OosTestCommands.RunAsync(args, services, output, error));
        Assert.Contains("explicit UTC offset", error.ToString());

        Assert.Equal(2, await OosTestCommands.RunAsync(
            Args("report.json").Where(value => value != "--allowed-risk" && value != "750").ToArray(),
            services, output, error));

        Assert.Equal(2, await OosTestCommands.RunAsync(
            [.. Args("report.json"), "--strategy-id", "not-a-strategy"], services, output, error));
        Assert.Contains("Unknown --strategy-id", error.ToString());
    }

    private static string[] Args(string destination) =>
    [
        "run-oos", "--instrument-id", InstrumentId.ToString(), "--timeframe", "5",
        "--from", "2026-09-01T00:00:00+05:30", "--to", "2026-09-03T00:00:00+05:30",
        "--training-sessions", "1", "--embargo-sessions", "0",
        "--initial-capital", "100000", "--allowed-risk", "750", "--maximum-capital", "100000",
        "--slippage-bps", "5", "--cost-profile", "none", "--output", destination
    ];

    private static Candle[] Candles() => Enumerable.Range(0, 2).SelectMany(day =>
        Enumerable.Range(0, 60).Select(bar => new Candle(InstrumentId, Timeframe.Minute5,
            Start.AddDays(day).AddMinutes(bar * 5), 100, 101, 99, 100, 100))).ToArray();
}
