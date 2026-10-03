# Upstox Historical Minute Data

This workflow downloads raw Upstox Historical Candle V3 data in inclusive monthly windows and imports verified manifest-listed CSV files. It supports minute intervals `1` and `5`; it does not replace or derive candles from existing 5-minute data. Upstox documents minute history from January 2022 and a maximum one-month request window for minute intervals 1 through 15. Dates on or after today in India Standard Time are rejected. A successful response with no candles is recorded as `empty`, not as completed coverage.

Official provider reference: [Historical Candle Data V3](https://upstox.com/developer/api-documentation/v3/get-historical-candle-data/). The documented route is `/v3/historical-candle/{instrument_key}/minutes/{interval}/{to_date}/{from_date}`. Upstox can change availability and retrieval limits; review its current documentation before a long backfill.

## Credentials And Instrument

Set the API token in the current PowerShell process only. The prompt masks input; the token is not included in command history, files, manifests, or downloader logs:

```powershell
$secureToken = Read-Host "Upstox access token" -AsSecureString
$tokenPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureToken)
try {
    $env:UPSTOX_ACCESS_TOKEN = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($tokenPointer)
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($tokenPointer)
    $secureToken.Dispose()
}
```

The access token must be valid when the request runs. A 401 or 403 stops immediately with a refresh instruction. The downloader never prints the token.

Look up the already-registered Nifty 50 row in SSMS using a read-only query. Confirm its exchange, symbol, name, and tick size; do not add a second Nifty instrument. No provider key is stored on the current `Instrument` entity, so imports require the explicit `--confirm-instrument-mapping` flag after this review.

```sql
SELECT [Id], [Exchange], [Symbol], [Name], [TickSize]
FROM [dbo].[Instruments]
WHERE [Exchange] = N'NSE'
  AND ([Symbol] LIKE N'%NIFTY%' OR [Name] LIKE N'%Nifty 50%')
ORDER BY [Symbol], [Name];
```

Capture the existing row's `Id` in PowerShell. Do not paste the GUID into tracked files:

```powershell
$instrumentId = Read-Host "Existing Nifty 50 Instrument Id from the query above"
[guid]::Parse($instrumentId) | Out-Null
```

The API uses the existing `ConnectionStrings__TradingDatabase` environment variable, Development User Secrets, or ignored `src/Trading.Api/appsettings.Local.json` according to the repository's normal configuration precedence. For a different local SQL Server, set `ConnectionStrings__TradingDatabase` in the current process or configure a local user secret. Never place connection credentials in tracked files or command examples. See [Market database setup](Market-setup.md).

For example, configure a local development connection through User Secrets without putting it in Git:

```powershell
dotnet user-secrets set "ConnectionStrings:TradingDatabase" "<local SQL Server connection string>" --project src/Trading.Api
```

The downloader's built-in usage is available without a token:

```powershell
dotnet run --project tools/Trading.DataDownloader -- --help
```

## Download And Import One Month

Run from the repository root. The base output directory produces `nse-index-nifty-50/1m/manifest.json` and monthly files below year/month directories. January 2025 is a completed historical month:

```powershell
$output = Join-Path (Get-Location) "data\upstox"
dotnet run --project tools/Trading.DataDownloader -- --instrument-key "NSE_INDEX|Nifty 50" --interval 1 --from 2025-01-01 --to 2025-01-31 --output "$output"
$manifest = Join-Path $output "nse-index-nifty-50\1m\manifest.json"
```

Validate files, registered-instrument tick size, and existing database values without writing any database rows:

```powershell
dotnet run --project src/Trading.Api -- import-candle-manifest --manifest "$manifest" --instrument-id $instrumentId --dry-run --confirm-instrument-mapping
```

Review the JSON report. It includes the provider key, registered instrument metadata, file/database validation status, actual range, zero/missing-volume statistics, identical existing rows, and estimated inserts. If index prices are not multiples of the registered tick size, import stops and reports the incompatibility; it never bypasses the existing tick rule.

Commit the import (or rerun this after an interrupted import). Each CSV is validated and committed in bounded batches. Existing identical candles are skipped; same-key/different-value candles are reported as conflicts and never overwritten:

```powershell
dotnet run --project src/Trading.Api -- import-candle-manifest --manifest "$manifest" --instrument-id $instrumentId --resume --confirm-instrument-mapping
```

`--dry-run` and `--resume` are mutually exclusive. The manifest determines timeframe; filenames are not used to infer it. The existing `import-candles` single-file command remains strict and unchanged.

## Download January 2022 Through A Chosen Date

Choose a completed date before today in India Standard Time. The command itself checks this too and does not silently shorten the requested range:

```powershell
$to = Read-Host "Completed end date in yyyy-MM-dd"
$toDate = [datetime]::ParseExact($to, "yyyy-MM-dd", [Globalization.CultureInfo]::InvariantCulture)
$india = [TimeZoneInfo]::FindSystemTimeZoneById("India Standard Time")
$indiaToday = [TimeZoneInfo]::ConvertTime([DateTimeOffset]::UtcNow, $india).Date
if ($toDate.Date -ge $indiaToday) { throw "Choose a completed date before today in India Standard Time." }
$output = Join-Path (Get-Location) "data\upstox"
dotnet run --project tools/Trading.DataDownloader -- --instrument-key "NSE_INDEX|Nifty 50" --interval 1 --from 2022-01-01 --to $to --output "$output"
```

The API is called once per clipped calendar month, using inclusive dates. A network/API failure is recorded per window and the process continues unless authentication fails or cancellation is requested. Empty windows and failed windows remain explicit work; an empty response is never treated as coverage. Data returned without volume is rejected rather than assigned fabricated volume. Zero-volume index bars are preserved and counted; they may be unsuitable for existing VWAP and other volume-dependent strategies.

## Resume

Use the same instrument, interval, requested range, and output directory. Download resume verifies the manifest identity and every completed file hash/row count before reuse; it retries unfinished/empty windows and reconciles identical atomic output left just before a manifest update. Changed output and incomplete temporary writes stop with an error rather than being trusted:

```powershell
dotnet run --project tools/Trading.DataDownloader -- --instrument-key "NSE_INDEX|Nifty 50" --interval 1 --from 2022-01-01 --to $to --output "$output" --resume
```

Database import resume uses the same command as commit above. It reconciles every bounded batch against the database, so a local progress marker is never treated as proof of commit:

```powershell
dotnet run --project src/Trading.Api -- import-candle-manifest --manifest "$manifest" --instrument-id $instrumentId --resume --confirm-instrument-mapping
```

The downloader's interval folder differs (`1m` versus `5m`), and generated filenames always include the actual interval. Existing 5-minute artifacts and rows are not replaced.

## Calendar Validation

Without a calendar, imports are permitted as raw data and report session completeness as `unverified`; they are not research-certified. An optional calendar invokes the existing `DatasetQualityCertifier`. The supplied inclusive scope must cover the full requested manifest range. For example, use the repository's 2025 NSE calendar only for a request contained within 2025:

```powershell
dotnet run --project src/Trading.Api -- import-candle-manifest --manifest "$manifest" --instrument-id $instrumentId --dry-run --confirm-instrument-mapping --calendar data/nse-calendar-2025.csv --calendar-id NSE-CM-2025 --calendar-from 2025-01-01 --calendar-to 2025-12-31
```

A calendar grid failure is reported separately and does not transform raw data into certified research data. Do not claim multi-year session completeness from a one-year calendar. Never invent holidays or special sessions to make certification pass.

The CLI checks the file's declared scope as well as the supplied `--calendar-from/to` dates. It accepts a title comment ending in a single year (as the checked-in 2025 calendar does), or a comment such as `# coverage,2022-01-01,2026-10-02` for a verified multi-year calendar. The scope declaration must reflect the actual source coverage; flags cannot extend it.

## Read-Only Database Checks

Candle `Timeframe` stores minute values (`1` and `5`). These queries only read SQL Server data. First verify that the selected existing Nifty instrument ID is the one returned by the lookup above.

```sql
DECLARE @InstrumentId uniqueidentifier = '00000000-0000-0000-0000-000000000000'; -- replace with the existing Nifty 50 Id

SELECT [Timeframe] AS [TimeframeMinutes], COUNT_BIG(*) AS [CandleCount],
       MIN([OpenTimeUtc]) AS [FirstOpenTimeUtc], MAX([OpenTimeUtc]) AS [LastOpenTimeUtc]
FROM [dbo].[Candles]
WHERE [InstrumentId] = @InstrumentId AND [Timeframe] = 1
GROUP BY [Timeframe];

SELECT [Timeframe] AS [TimeframeMinutes], COUNT_BIG(*) AS [CandleCount],
       MIN([OpenTimeUtc]) AS [FirstOpenTimeUtc], MAX([OpenTimeUtc]) AS [LastOpenTimeUtc]
FROM [dbo].[Candles]
WHERE [InstrumentId] = @InstrumentId AND [Timeframe] = 5
GROUP BY [Timeframe];
```

The database primary key is `(InstrumentId, Timeframe, OpenTimeUtc)`, so minute-1 and minute-5 bars at the same instant are distinct. Check for duplicate keys and inspect the unique primary-key columns with these read-only queries:

```sql
SELECT [InstrumentId], [Timeframe], [OpenTimeUtc], COUNT_BIG(*) AS [DuplicateCount]
FROM [dbo].[Candles]
WHERE [InstrumentId] = @InstrumentId
GROUP BY [InstrumentId], [Timeframe], [OpenTimeUtc]
HAVING COUNT_BIG(*) > 1;

SELECT i.[name] AS [IndexName], i.[is_primary_key], i.[is_unique], c.[name] AS [KeyColumn], ic.[key_ordinal]
FROM sys.indexes AS i
JOIN sys.index_columns AS ic ON ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
JOIN sys.columns AS c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
WHERE i.[object_id] = OBJECT_ID(N'dbo.Candles') AND ic.[key_ordinal] > 0
ORDER BY i.[is_primary_key] DESC, ic.[key_ordinal];
```

No historical download or production database import is run by the automated test suite. Tests use mocked HTTP responses, temporary files, and an in-memory SQLite database. A successful fixture test is not evidence of live provider availability or completed database ingestion.
