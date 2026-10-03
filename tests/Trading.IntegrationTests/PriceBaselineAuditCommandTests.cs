using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Trading.Api;
using Trading.Application.MarketData;
using Trading.Domain.MarketData;
using Trading.Infrastructure.Persistence;

namespace Trading.IntegrationTests;

public sealed class PriceBaselineAuditCommandTests
{
    private static readonly Guid InstrumentId = Guid.Parse("7787840d-44f9-4b47-b8ee-6c0ac40c7a01");
    private static readonly DateTimeOffset Open = DateTimeOffset.Parse("2025-01-02T09:15:00+05:30");

    [Fact]
    public async Task Audit_writes_provisional_timeframe_separated_price_baseline()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        await using var services = new ServiceCollection().AddDbContext<TradingDbContext>(options => options.UseSqlite(connection))
            .AddScoped<IMarketDataStore, MarketDataStore>().BuildServiceProvider();
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            await db.Database.EnsureCreatedAsync();
            var store = new MarketDataStore(db);
            await store.AddInstrumentAsync(new(InstrumentId, "NSE", "NIFTY50", "Nifty 50", 1, .05m));
            await store.AddCandlesAsync([
                new(InstrumentId, Timeframe.Minute1, Open, 100, 102, 99, 101, 0),
                new(InstrumentId, Timeframe.Minute1, Open.AddMinutes(1), 101, 103, 100, 102, 5),
                new(InstrumentId, Timeframe.Minute5, Open, 200, 202, 199, 201, 10),
                new(InstrumentId, Timeframe.Minute1, Open.AddDays(-1), 100, 101, 99, 100, 5)
            ]);
        }

        var directory = Path.Combine(Path.GetTempPath(), "price-baseline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "baseline.json");
        try
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var exitCode = await PriceBaselineAuditCommands.RunAsync(Args(destination), services, output, error);
            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(destination));
            var root = document.RootElement;
            Assert.Equal("provisional", root.GetProperty("status").GetString());
            Assert.False(root.GetProperty("researchCertified").GetBoolean());
            Assert.Equal("2025-01-02", root.GetProperty("range").GetProperty("from").GetString());
            var groups = root.GetProperty("timeframes").EnumerateArray().ToDictionary(
                item => item.GetProperty("timeframeMinutes").GetInt32());
            Assert.Equal(2, groups[1].GetProperty("candleCount").GetInt32());
            Assert.Equal(1, groups[5].GetProperty("candleCount").GetInt32());
            Assert.Equal(1, groups[1].GetProperty("zeroVolumeRows").GetInt32());
            Assert.Equal(99m, groups[1].GetProperty("priceMin").GetDecimal());
            Assert.Equal(202m, groups[5].GetProperty("priceMax").GetDecimal());
            Assert.Contains("price-baseline-audit-created", output.ToString());
            Assert.Equal(2, await PriceBaselineAuditCommands.RunAsync(Args(destination), services, output, error));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Invalid_arguments_are_rejected_before_database_access()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(2, await PriceBaselineAuditCommands.RunAsync(
            ["audit-price-baseline", "--instrument-id", InstrumentId.ToString(), "--from", "2025-02-01",
                "--to-exclusive", "2025-01-01", "--output", "report.json"], services, output, error));
        Assert.Contains("--from must precede --to-exclusive", error.ToString());
        Assert.Equal(2, await PriceBaselineAuditCommands.RunAsync(
            [.. Args("report.json"), "--unexpected", "true"], services, output, error));
        Assert.Contains("Unknown option", error.ToString());
    }

    [Fact]
    public async Task Relative_report_path_resolves_from_repository_root()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        await using var services = new ServiceCollection().AddDbContext<TradingDbContext>(options => options.UseSqlite(connection))
            .AddScoped<IMarketDataStore, MarketDataStore>().BuildServiceProvider();
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            await db.Database.EnsureCreatedAsync();
            await new MarketDataStore(db).AddInstrumentAsync(new(InstrumentId, "NSE", "NIFTY50", "Nifty 50", 1, .05m));
        }

        var repositoryRoot = FindRepositoryRoot();
        var reportsDirectory = Path.Combine(repositoryRoot, "reports");
        var createdDirectory = !Directory.Exists(reportsDirectory);
        Directory.CreateDirectory(reportsDirectory);
        var filename = $"price-baseline-test-{Guid.NewGuid():N}.json";
        var destination = Path.Combine(reportsDirectory, filename);
        try
        {
            var exitCode = await PriceBaselineAuditCommands.RunAsync(Args(Path.Combine("reports", filename)),
                services, TextWriter.Null, TextWriter.Null);
            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(destination));
        }
        finally
        {
            if (File.Exists(destination)) File.Delete(destination);
            if (createdDirectory && !Directory.EnumerateFileSystemEntries(reportsDirectory).Any()) Directory.Delete(reportsDirectory);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TradingCommandCenter.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("TradingCommandCenter.sln could not be located.");
    }

    private static string[] Args(string destination) =>
    [
        "audit-price-baseline", "--instrument-id", InstrumentId.ToString(),
        "--from", "2025-01-02", "--to-exclusive", "2025-01-03", "--output", destination
    ];
}