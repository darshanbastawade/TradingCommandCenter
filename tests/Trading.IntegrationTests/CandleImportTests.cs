using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Trading.Api;
using Trading.Application.MarketData;
using Trading.Domain.MarketData;
using Trading.Infrastructure.Persistence;
using Trading.MarketData.Import;

namespace Trading.IntegrationTests;

public sealed class CandleImportTests
{
    private static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-02T00:00:00Z");
    private const string Row = "2026-09-01T09:15:00+05:30,100,102,99,101,1000,";
    private const string Csv = CandleCsvReader.Header + "\n" + Row;

    [Fact]
    public async Task Imports_once_and_rejects_overlap_without_changing_evidence()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var store = new MarketDataStore(db);
        await store.AddInstrumentAsync(new Instrument(Id, "TEST", "SYNTHETIC", "Synthetic", 1, .05m));
        var importer = new HistoricalCandleImporter(store);
        var result = await importer.ImportAsync(new StringReader(Csv), Id, Timeframe.Minute5, Now);
        Assert.Single(result.Candles);
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<CandleImportException>(() => importer.ImportAsync(new StringReader(Csv + "\n" + Row.Replace("09:15", "09:20")), Id, Timeframe.Minute5, Now));
        Assert.Equal(1, await db.Candles.CountAsync());
    }

    [Fact]
    public async Task Rejects_invalid_final_row_unknown_instrument_and_wrong_tick_without_writes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var store = new MarketDataStore(db);
        var importer = new HistoricalCandleImporter(store);
        await Assert.ThrowsAsync<CandleImportException>(() => importer.ImportAsync(new StringReader(Csv), Id, Timeframe.Minute5, Now));
        await store.AddInstrumentAsync(new Instrument(Id, "TEST", "SYNTHETIC", "Synthetic", 1, .05m));
        await Assert.ThrowsAsync<CandleImportException>(() => importer.ImportAsync(new StringReader(Csv + "\ninvalid"), Id, Timeframe.Minute5, Now));
        await Assert.ThrowsAsync<CandleImportException>(() => importer.ImportAsync(new StringReader(Csv.Replace(",101,", ",101.01,")), Id, Timeframe.Minute5, Now));
        Assert.Empty(await db.Candles.ToListAsync());
    }

    [Fact]
    public async Task CLI_registers_instrument_and_reports_unknown_arguments()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var services = new ServiceCollection()
            .AddDbContext<TradingDbContext>(options => options.UseSqlite(connection))
            .AddScoped<IMarketDataStore, MarketDataStore>().BuildServiceProvider();
        using (var scope = services.CreateScope()) await scope.ServiceProvider.GetRequiredService<TradingDbContext>().Database.EnsureCreatedAsync();
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await MarketDataCommands.RunAsync(["add-instrument", "--instrument-id", Id.ToString(), "--exchange", "TEST",
            "--symbol", "SYNTHETIC", "--name", "Synthetic", "--lot-size", "1", "--tick-size", "0.05"], services, output, error);
        Assert.Equal(0, exit);
        Assert.Contains("registered", output.ToString());
        Assert.Equal(2, await MarketDataCommands.RunAsync(["import-candles", "--typo", "x"], services, output, error));
    }

    [Fact]
    public async Task CLI_dry_run_needs_no_store_but_commit_persists()
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, Csv);
            var args = new[] { "import-candles", "--file", file, "--instrument-id", Id.ToString(), "--timeframe", "5" };
            await using var emptyServices = new ServiceCollection().BuildServiceProvider();
            var output = new StringWriter();
            var error = new StringWriter();
            Assert.Equal(0, await MarketDataCommands.RunAsync([..args, "--dry-run"], emptyServices, output, error));
            Assert.Contains("validated-file-only", output.ToString());

            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var services = new ServiceCollection().AddDbContext<TradingDbContext>(options => options.UseSqlite(connection))
                .AddScoped<IMarketDataStore, MarketDataStore>().BuildServiceProvider();
            using (var scope = services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                await db.Database.EnsureCreatedAsync();
                await new MarketDataStore(db).AddInstrumentAsync(new Instrument(Id, "TEST", "SYNTHETIC", "Synthetic", 1, .05m));
            }
            Assert.Equal(0, await MarketDataCommands.RunAsync(args, services, output, error));
            Assert.Contains("imported", output.ToString());
            using var check = services.CreateScope();
            Assert.Equal(1, await check.ServiceProvider.GetRequiredService<TradingDbContext>().Candles.CountAsync());
        }
        finally { File.Delete(file); }
    }
}
