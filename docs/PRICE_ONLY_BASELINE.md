# Nifty price-only research baseline

This is a read-only audit and signal compatibility diagnostic, not a profitability backtest or live-trading integration. Existing strategy IDs and the default catalog retain their current behavior. No raw 1-minute or 5-minute rows are changed. No migration is needed.

## Run locally

Use the existing database connection configuration and .NET 10 SDK. From the repository root, run in PowerShell:

```powershell
dotnet test tests/Trading.UnitTests --filter FullyQualifiedName~ResearchBaselineTests
dotnet test tests/Trading.StrategyValidationTests --filter FullyQualifiedName~PriceOnlyStrategyTests
dotnet test tests/Trading.IntegrationTests --filter FullyQualifiedName~CandleManifestImporterTests
New-Item -ItemType Directory -Force reports
dotnet run --project src/Trading.Api -- audit-price-baseline --instrument-id 7787840d-44f9-4b47-b8ee-6c0ac40c7a01 --from 2022-01-01 --to-exclusive 2026-10-03 --output reports/nifty-baseline-provisional.json
```

The end date is exclusive in IST. Use a new output filename for each run. The initial command returns exit code 2 with a JSON report: this is expected for a provisional calendar. Database row count should remain 440,167 if the selected range and stored data are unchanged. This command cannot validate the user's database until it is run against that database.

Inspect `statusCounts`, `days`, `RawRows`, `FiveMinuteRows`, and `CalendarVerified`. UTC timestamps in `MissingUtc`/`OutsideSessionUtc` can be converted to IST by adding 05:30. The report includes every requested date, including dates without any rows. A weekday without data is initially `unverified-gap-or-holiday`; it is not automatically treated as a holiday.

## Verify the calendar before enabling strategy diagnostics

Extract the report's calendar as a separate JSON file:

```powershell
$r = Get-Content reports/nifty-baseline-provisional.json -Raw | ConvertFrom-Json
$r.calendar | ConvertTo-Json -Depth 10 | Set-Content reports/nse-calendar-review.json -Encoding utf8
```

Review **every date** against exchange calendar notices for the entire requested period. Include exceptional closures and additional trading Saturdays. For closures use `"Windows": []`; for trading dates use explicit IST windows with exclusive close times. Record the source URLs/document references in `Sources`. Only after checking the full calendar set `Verified` to `true`. This flag is an operator attestation; the tool does not verify URLs. Do not infer closures from absent database rows or simply flip the flag to force a pass.

Example of a split session entry:

```json
{"Date":"2024-03-02","Windows":[{"Open":"09:15:00","Close":"10:00:00"},{"Open":"11:30:00","Close":"12:30:00"}]}
```

The provisional template includes the six special dates discussed in the data review. It is **not** a complete 2022–2026 holiday calendar. Relevant notices include:

- https://zerodha.com/marketintel/bulletin/334061/muhurat-trading-session-on-account-of-diwali-6
- https://zerodha.com/marketintel/bulletin/363312/muhurat-trading-session-on-account-of-diwali-7
- https://zerodha.com/marketintel/bulletin/371327/live-trading-session-on-saturday-march-02-2024
- https://zerodha.com/marketintel/bulletin/377895/live-trading-session-on-saturday-may-18-2024
- https://zerodha.com/z-connect/updates/muhurat-trading-session-on-friday-november-1-2024
- https://tradingqna.com/t/muhurat-trading-session-samvat-2082-october-21-2025/187303

Run again with the reviewed calendar:

```powershell
dotnet run --project src/Trading.Api -- audit-price-baseline --instrument-id 7787840d-44f9-4b47-b8ee-6c0ac40c7a01 --from 2022-01-01 --to-exclusive 2026-10-03 --calendar reports/nse-calendar-review.json --output reports/nifty-baseline-reviewed.json
```

## Baseline policy

- Exclude the entire dates 2022-03-07, 2024-12-12, 2025-03-25, 2025-04-04, 2025-04-23, which contain the seven accepted missing minutes. Their anomalies remain visible in the report.
- Audit split/special sessions using their actual windows. For this first baseline, exclude **all nonstandard sessions** from strategy evaluation; retain their raw data. Thus the two scheduled 90-minute breaks do not become data gaps.
- Exclude outside-session rows from aggregation while reporting them. Complete selected-session data does not mean raw data has no extra rows.
- Block compatibility evaluation for unexpected missing days/minutes, duplicate or misaligned timestamps. No padding, forward-filling or partial five-minute bars.
- Aggregate regular sessions from 09:15 IST using first open, maximum high, minimum low, last close, summed volume, and last open interest. Derived bars stay in memory; existing imported 5-minute data remains untouched.
- Carry indicator history across declared closures. Reset after excluded/invalid dates, and warm up again before emitting signals (EMA50 needs 50 five-minute bars). The report records block sizes and a deterministic fingerprint of the selected data, calendar and policy.

Three separate diagnostic variants use complete five-minute bars: `opening-range-breakout-price-only-v1`, `ema-pullback-continuation-price-only-v1`, and `adx-trend-continuation-price-only-v1`. They do not require volume or VWAP; those evidence fields are null when unavailable. The two VWAP strategies are deferred. Strategy counts are candidate signals, not executed trades. Zero signals does not itself prove incompatibility.

The variants are intentionally isolated from the default catalog, native engine factory and Python/LEAN adapters. Full execution simulation, cross-engine parity and performance evaluation are subsequent work. Signal timestamps retain the existing bar-open convention; prices use the completed close and must not be treated as available at the bar's opening time.

A separate importer regression fix uses the final file's end timestamp when validating a multi-file manifest window.

## Validation status

Targeted unit, strategy and SQLite integration tests are included. They must be run locally: the implementation environment did not have a .NET SDK, so no build or test pass is claimed. Run the full existing test suite before merging because the evidence contract now allows nullable VWAP and average-volume values.
