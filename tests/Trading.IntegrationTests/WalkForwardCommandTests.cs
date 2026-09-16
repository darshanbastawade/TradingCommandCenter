using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Trading.Api;
using Trading.Application.MarketData;
using Trading.Domain.MarketData;
using Trading.Infrastructure.Persistence;

namespace Trading.IntegrationTests;

public sealed class WalkForwardCommandTests
{
    private static readonly Guid InstrumentId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T09:15:00+05:30");

    [Theory]
    [InlineData("rolling", "rolling")]
    [InlineData("anchored", "anchored")]
    public async Task Cli_runs_sql_backed_walk_forward_and_writes_fold_and_combined_reports(
        string requestedMode, string expectedMode)
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
            await store.AddInstrumentAsync(new(InstrumentId, "NSE", "SYNTHETIC-WF", "Synthetic walk-forward", 50, .05m));
            await store.AddCandlesAsync(Candles());
        }

        var destination = Path.Combine(Path.GetTempPath(), $"walk-forward-{Guid.NewGuid():N}.json");
        try
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var exit = await WalkForwardCommands.RunAsync(Args(destination, requestedMode), services, output, error);

            Assert.Equal(0, exit);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(destination));
            var root = document.RootElement;
            Assert.Equal("chronological-walk-forward", root.GetProperty("testType").GetString());
            Assert.Equal(expectedMode, root.GetProperty("trainingMode").GetString());
            Assert.Equal(8 * 60, root.GetProperty("sourceCandleCount").GetInt32());
            Assert.Equal(2, root.GetProperty("foldCount").GetInt32());
            Assert.Equal(1, root.GetProperty("unusedTrailingSessionCount").GetInt32());
            Assert.Equal(2, root.GetProperty("folds").GetArrayLength());
            Assert.Equal(2, root.GetProperty("folds")[0].GetProperty("window")
                .GetProperty("testingSessionCount").GetInt32());
            Assert.Equal(requestedMode == "anchored" ? 240 : 120,
                root.GetProperty("folds")[1].GetProperty("trainingCandleCount").GetInt32());
            Assert.Equal(4, root.GetProperty("combinedReport").GetProperty("summary")
                .GetProperty("equityCurve").GetArrayLength());
            Assert.Contains("walk-forward-report-created", output.ToString());
            Assert.Equal(string.Empty, error.ToString());

            Assert.Equal(2, await WalkForwardCommands.RunAsync(
                Args(destination, requestedMode), services, output, error));
        }
        finally
        {
            if (File.Exists(destination)) File.Delete(destination);
        }
    }

    [Fact]
    public async Task Cli_rejects_invalid_training_mode_before_database_access()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        var error = new StringWriter();
        var exit = await WalkForwardCommands.RunAsync(
            Args(Path.Combine(Path.GetTempPath(), $"walk-forward-{Guid.NewGuid():N}.json"), "expanding"),
            services, TextWriter.Null, error);

        Assert.Equal(2, exit);
        Assert.Contains("rolling or anchored", error.ToString());
    }

    private static string[] Args(string destination, string mode) =>
    [
        "run-walk-forward", "--instrument-id", InstrumentId.ToString(), "--timeframe", "5",
        "--from", "2026-09-01T00:00:00+05:30", "--to", "2026-09-09T00:00:00+05:30",
        "--training-sessions", "2", "--testing-sessions", "2", "--embargo-sessions", "1",
        "--training-mode", mode, "--initial-capital", "100000", "--allowed-risk", "750",
        "--maximum-capital", "100000", "--slippage-bps", "5", "--cost-profile", "none",
        "--output", destination
    ];

    private static Candle[] Candles() => Enumerable.Range(0, 8).SelectMany(day =>
        Enumerable.Range(0, 60).Select(bar => new Candle(InstrumentId, Timeframe.Minute5,
            Start.AddDays(day).AddMinutes(bar * 5), 100, 101, 99, 100, 100))).ToArray();
}
