using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Trading.Api;
using Trading.Application.MarketData;
using Trading.Domain.MarketData;
using Trading.Infrastructure.Persistence;
using Trading.MarketData.Import;
using Trading.MarketData.Quality;

namespace Trading.IntegrationTests;

public sealed class CandleManifestImporterTests
{
    private static readonly Guid InstrumentId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Open = DateTimeOffset.Parse("2025-01-02T09:15:00+05:30");
    private const string CsvRow = "2025-01-02T09:15:00+05:30,100,102,99,101,10,";
    private const string Csv = CandleCsvReader.Header + "\n" + CsvRow + "\n";

    [Fact]
    public async Task Manifest_dry_run_writes_nothing_then_identical_import_skips_and_timeframes_coexist()
    {
        var root = NewDirectory();
        await using var database = await TestDatabase.CreateAsync();
        try
        {
            await database.Store.AddInstrumentAsync(new Instrument(InstrumentId, "NSE", "NIFTY50", "Nifty 50", 1, .05m));
            await database.Store.AddCandlesAsync([new(InstrumentId, Timeframe.Minute5, Open, 100, 102, 99, 101, 10)]);
            var manifest = await CreateManifestAsync(root);
            var output = new StringWriter();
            var error = new StringWriter();
            await using var services = new ServiceCollection().AddDbContext<TradingDbContext>(options => options.UseSqlite(database.Connection))
                .AddScoped<IMarketDataStore, MarketDataStore>().BuildServiceProvider();
            var missingConfirmation = await MarketDataCommands.RunAsync(["import-candle-manifest", "--manifest", manifest,
                "--instrument-id", InstrumentId.ToString(), "--dry-run"], services, output, error);
            Assert.Equal(2, missingConfirmation);
            Assert.Contains("explicitly pass --confirm-instrument-mapping", error.ToString());
            var dryRunExit = await MarketDataCommands.RunAsync(["import-candle-manifest", "--manifest", manifest,
                "--instrument-id", InstrumentId.ToString(), "--dry-run", "--confirm-instrument-mapping"], services, output, error);
            Assert.Equal(0, dryRunExit);
            Assert.Contains("validated-no-writes", output.ToString());
            Assert.Equal(1, await database.Context.Candles.CountAsync());

            var importer = new CandleManifestImporter(database.Store);
            var first = await importer.ImportAsync(manifest, InstrumentId, true, DateTimeOffset.UtcNow);
            Assert.Equal(1, first.NewlyInsertedRows);
            Assert.Equal(0, first.IdenticalSkippedRows);
            var second = await importer.ImportAsync(manifest, InstrumentId, true, DateTimeOffset.UtcNow);
            Assert.Equal(0, second.NewlyInsertedRows);
            Assert.Equal(1, second.IdenticalSkippedRows);
            Assert.Equal(2, await database.Context.Candles.CountAsync());
            Assert.Equal(1, await database.Context.Candles.CountAsync(x => x.Timeframe == Timeframe.Minute1));
            Assert.Equal(1, await database.Context.Candles.CountAsync(x => x.Timeframe == Timeframe.Minute5));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Different_values_at_an_existing_key_fail_without_overwriting()
    {
        var root = NewDirectory();
        await using var database = await TestDatabase.CreateAsync();
        try
        {
            await database.Store.AddInstrumentAsync(new Instrument(InstrumentId, "NSE", "NIFTY50", "Nifty 50", 1, .05m));
            await database.Store.AddCandlesAsync([new(InstrumentId, Timeframe.Minute1, Open, 100, 102, 98, 99, 10)]);
            var manifest = await CreateManifestAsync(root);
            var importer = new CandleManifestImporter(database.Store);
            var exception = await Assert.ThrowsAsync<CandleManifestConflictException>(() => importer.ImportAsync(manifest,
                InstrumentId, true, DateTimeOffset.UtcNow));
            Assert.Contains("Conflicting existing candle", exception.Message);
            Assert.Equal(1, exception.Conflicts);
            Assert.Equal(1, exception.Summary.DownloadedRows);
            Assert.Equal(1, exception.Summary.Conflicts);
            var stored = await database.Store.ReadCandlesAsync(InstrumentId, Timeframe.Minute1, Open, Open.AddMinutes(1));
            Assert.Equal(99m, Assert.Single(stored).Close);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Scoped_calendar_runs_existing_certifier_but_never_claims_research_certification()
    {
        var root = NewDirectory();
        await using var database = await TestDatabase.CreateAsync();
        try
        {
            await database.Store.AddInstrumentAsync(new Instrument(InstrumentId, "NSE", "NIFTY50", "Nifty 50", 1, .05m));
            var manifest = await CreateManifestAsync(root);
            var india = TimeZoneInfo.CreateCustomTimeZone("Test India", TimeSpan.FromMinutes(330), "Test India", "Test India");
            var calendar = new ExchangeSessionCalendar("fixture-2025", india, new(9, 15), new(15, 30),
                new HashSet<DateOnly>(), new Dictionary<DateOnly, ExchangeSession>());
            var importer = new CandleManifestImporter(database.Store);
            var tooNarrow = await Assert.ThrowsAsync<CandleImportException>(() => importer.ImportAsync(manifest,
                InstrumentId, false, DateTimeOffset.UtcNow, calendar, new(2025, 1, 3), new(2025, 1, 31)));
            Assert.Contains("coverage", tooNarrow.Message);
            var summary = await importer.ImportAsync(manifest, InstrumentId, false, DateTimeOffset.UtcNow,
                calendar, new(2025, 1, 1), new(2025, 12, 31));
            Assert.Equal("calendar-validation-failed", summary.CalendarValidationStatus);
            Assert.False(summary.ResearchCertified);
            Assert.Empty(await database.Context.Candles.ToListAsync());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CLI_rejects_calendar_flags_that_overstate_file_coverage()
    {
        var root = NewDirectory();
        var calendarRoot = NewDirectory();
        await using var database = await TestDatabase.CreateAsync();
        try
        {
            await database.Store.AddInstrumentAsync(new Instrument(InstrumentId, "NSE", "NIFTY50", "Nifty 50", 1, .05m));
            var manifest = await CreateManifestAsync(root);
            var calendarFile = Path.Combine(calendarRoot, "calendar.csv");
            await File.WriteAllTextAsync(calendarFile, "# NSE Capital Market calendar - 2025\n");
            await using var services = new ServiceCollection().AddDbContext<TradingDbContext>(options => options.UseSqlite(database.Connection))
                .AddScoped<IMarketDataStore, MarketDataStore>().BuildServiceProvider();
            var output = new StringWriter();
            var error = new StringWriter();
            var args = new[]
            {
                "import-candle-manifest", "--manifest", manifest, "--instrument-id", InstrumentId.ToString(),
                "--dry-run", "--confirm-instrument-mapping", "--calendar", calendarFile,
                "--calendar-id", "fixture-2025", "--calendar-from", "2025-01-01", "--calendar-to", "2025-12-31"
            };
            var exitCode = await MarketDataCommands.RunAsync(args, services, output, error);
            Assert.True(exitCode == 0, error.ToString());
            Assert.Contains("calendar-validation-failed", output.ToString());

            var overstated = args.ToArray();
            overstated[Array.IndexOf(overstated, "2025-01-01")] = "2022-01-01";
            overstated[Array.IndexOf(overstated, "2025-12-31")] = "2026-10-02";
            Assert.Equal(2, await MarketDataCommands.RunAsync(overstated, services, output, error));
            Assert.Contains("exceeds the file's declared coverage", error.ToString());
            Assert.Empty(await database.Context.Candles.ToListAsync());
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(calendarRoot, true);
        }
    }

    [Fact]
    public async Task Corrupt_manifest_path_traversal_and_unlisted_files_are_rejected()
    {
        var root = NewDirectory();
        await using var database = await TestDatabase.CreateAsync();
        try
        {
            await database.Store.AddInstrumentAsync(new Instrument(InstrumentId, "NSE", "NIFTY50", "Nifty 50", 1, .05m));
            var manifest = await CreateManifestAsync(root);
            await File.WriteAllTextAsync(manifest, "{");
            await Assert.ThrowsAsync<JsonException>(() => new CandleManifestImporter(database.Store)
                .ImportAsync(manifest, InstrumentId, false, DateTimeOffset.UtcNow));

            manifest = await CreateManifestAsync(root);
            var contents = await File.ReadAllTextAsync(manifest);
            contents = contents.Replace("2025/01/nifty-index-1m-2025-01-01.csv", "../outside.csv", StringComparison.Ordinal);
            await File.WriteAllTextAsync(manifest, contents);
            await Assert.ThrowsAsync<CandleImportException>(() => new CandleManifestImporter(database.Store)
                .ImportAsync(manifest, InstrumentId, false, DateTimeOffset.UtcNow));

            manifest = await CreateManifestAsync(root);
            await File.WriteAllTextAsync(Path.Combine(root, "unlisted.csv"), Csv);
            var exception = await Assert.ThrowsAsync<CandleImportException>(() => new CandleManifestImporter(database.Store)
                .ImportAsync(manifest, InstrumentId, false, DateTimeOffset.UtcNow));
            Assert.Contains("Unmanifested CSV", exception.Message);
            Assert.Empty(await database.Context.Candles.ToListAsync());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Corrupt_hash_and_underscoped_calendar_are_rejected_before_database_writes()
    {
        var root = NewDirectory();
        await using var database = await TestDatabase.CreateAsync();
        try
        {
            await database.Store.AddInstrumentAsync(new Instrument(InstrumentId, "NSE", "NIFTY50", "Nifty 50", 1, .05m));
            var manifest = await CreateManifestAsync(root);
            await File.AppendAllTextAsync(Path.Combine(root, "2025", "01", "nifty-index-1m-2025-01-01.csv"), "tamper");
            await Assert.ThrowsAsync<CandleImportException>(() => new CandleManifestImporter(database.Store)
                .ImportAsync(manifest, InstrumentId, true, DateTimeOffset.UtcNow));
            Assert.Empty(await database.Context.Candles.ToListAsync());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Import_reconciles_a_commit_that_succeeded_before_the_connection_failed()
    {
        var root = NewDirectory();
        await using var database = await TestDatabase.CreateAsync();
        try
        {
            await database.Store.AddInstrumentAsync(new Instrument(InstrumentId, "NSE", "NIFTY50", "Nifty 50", 1, .05m));
            var manifest = await CreateManifestAsync(root);
            var uncertainStore = new CommitThenThrowStore(database.Store);
            var summary = await new CandleManifestImporter(uncertainStore).ImportAsync(manifest, InstrumentId,
                true, DateTimeOffset.UtcNow);
            Assert.Equal(0, summary.NewlyInsertedRows);
            Assert.Equal(1, summary.IdenticalSkippedRows);
            Assert.Equal(1, await database.Context.Candles.CountAsync());
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<string> CreateManifestAsync(string root)
    {
        var folder = Path.Combine(root, "2025", "01");
        Directory.CreateDirectory(folder);
        var relative = "2025/01/nifty-index-1m-2025-01-01.csv";
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        await File.WriteAllTextAsync(path, Csv, new UTF8Encoding(false));
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant();
        var manifest = new
        {
            schemaVersion = 2,
            provider = "Upstox",
            providerApi = "historical-candle-v3",
            instrumentKey = "NSE_INDEX|Nifty 50",
            intervalMinutes = 1,
            requestedFrom = new DateOnly(2025, 1, 2),
            requestedTo = new DateOnly(2025, 1, 2),
            actualFrom = Open,
            actualTo = Open,
            downloadedAtUtc = DateTimeOffset.UtcNow,
            totalRows = 1,
            zeroVolumeRows = 0,
            missingVolumeRows = 0,
            sessionCompleteness = "unverified",
            coverageWarnings = new[] { "Calendar coverage is unverified." },
            windows = new[]
            {
                new
                {
                    from = new DateOnly(2025, 1, 2),
                    to = new DateOnly(2025, 1, 2),
                    status = "completed",
                    rowCount = 1,
                    firstTimestamp = Open,
                    lastTimestamp = Open,
                    files = new[]
                    {
                        new { relativePath = relative, rowCount = 1, firstTimestamp = Open, lastTimestamp = Open, sha256 = hash, zeroVolumeRows = 0 }
                    }
                }
            }
        };
        var manifestPath = Path.Combine(root, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        return manifestPath;
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "trading-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private TestDatabase(SqliteConnection connection, TradingDbContext context)
        {
            Connection = connection;
            Context = context;
            Store = new MarketDataStore(context);
        }

        public SqliteConnection Connection { get; }
        public TradingDbContext Context { get; }
        public MarketDataStore Store { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync();
            var context = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            return new(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed class CommitThenThrowStore(IMarketDataStore inner) : IMarketDataStore
    {
        private bool _throwAfterCommit = true;
        public Task AddInstrumentAsync(Instrument instrument, CancellationToken cancellationToken = default) => inner.AddInstrumentAsync(instrument, cancellationToken);
        public Task<Instrument?> FindInstrumentAsync(Guid id, CancellationToken cancellationToken = default) => inner.FindInstrumentAsync(id, cancellationToken);
        public async Task AddCandlesAsync(IReadOnlyCollection<Candle> candles, CancellationToken cancellationToken = default)
        {
            await inner.AddCandlesAsync(candles, cancellationToken);
            if (_throwAfterCommit)
            {
                _throwAfterCommit = false;
                throw new IOException("Simulated lost acknowledgement after DB commit.");
            }
        }
        public Task<IReadOnlyList<Candle>> ReadCandlesAsync(Guid instrumentId, Timeframe timeframe,
            DateTimeOffset from, DateTimeOffset to, int limit = 10000, CancellationToken cancellationToken = default) =>
            inner.ReadCandlesAsync(instrumentId, timeframe, from, to, limit, cancellationToken);
    }
}