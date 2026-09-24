# Verification — September 24, 2026

## M38.11 end-to-end production safety acceptance

- Added a deterministic integration fixture spanning M16 certified dataset identity, M27 retained candidate, M28 native verification, genuine-provenance M30 comparison, M31 robustness, M32 certificate, bound M33 fixture analysis, M34 human review, ten post-M34 schema-v2 M22 sessions, M35 durable qualification, authoritative M36 provenance, the production M37 command, fresh M18 approval, atomic M37 consumption, and M23 fake-broker submission.
- The positive path reaches `PlaceLimitBuyAsync` exactly once and consumes exactly one M37 authorization. AI, LEAN and broker behavior are sealed fixtures or fakes; no external process or service is invoked.
- Added twenty single-fault cases for dataset/specification identity, M30 verdict, robustness, M32 status/expiry, M34, M35, M36, M37 decision/expiry/replay/unresolved state, M18, kill switch, direct-order opt-in, operator confirmation, broker-data freshness and unexpected position. Every case asserts zero fake-broker submission calls.
- Added a source architecture assertion requiring exactly one production `.PlaceLimitBuyAsync(` call site and requiring M37 verification, `LiveTradingEngine.Prepare` (M18), and atomic `TryConsumeAsync` to precede it. The asserted call site is `LiveTradingCommands.RunAsync`.
- The suite exposed and fixed an M32 CLI response defect where `status` and `certificate.Status` collided after camel-case conversion. The response now uses distinct `eventStatus` and `certificateStatus` fields; certificate evidence and hashes are unchanged.
- Locked restore passed. The full Release solution built with 0 warnings and 0 errors. All 351 tests passed: BacktestTests 111, IntegrationTests 133, StrategyValidationTests 48, UnitTests 59. No tests were skipped. The dedicated M38.11 acceptance filter passed 22/22 cases.
- No SQL Server, Zerodha, Azure OpenAI, paid AI, Docker, LEAN CLI, broker, or other external network call occurred. No database migration was added or applied. M38 closure proves the implemented enforcement chain; it does not imply strategy profitability.

## M38.10 CI and verification closure

- Added `.github/workflows/ci.yml` with one branch-protection-ready `CI / verify` status on every push and pull request. It installs the exact `10.0.401` SDK read from `global.json`, performs locked single-node restore, builds the full Release solution without restoring, explicitly runs all five M38.8 parity fixtures, runs the complete .NET suite without rebuilding, and retains TRX results.
- The same job installs Python 3.13 and the exact `==` dependency set from `workers/vectorbt/requirements.lock.txt`, syntax-compiles the worker/port, and runs the real vectorbt worker through `workers/vectorbt/smoke_test.py`. All Azure OpenAI and Zerodha values are absent and the live kill switch/order gates are explicitly fail-closed.
- Added the optional manual `.github/workflows/lean-validation.yml`. It accepts only an immutable image digest and a pre-staged M29 package on a dedicated `lean-validation` self-hosted runner, invokes the official LEAN CLI, and retains raw official output/evidence. It never runs on push or pull request and all live broker/feed gates are forced closed.
- Local locked restore passed. The full Release solution built with 0 warnings and 0 errors. All 329 tests passed: BacktestTests 111, IntegrationTests 111, StrategyValidationTests 48, UnitTests 59. No tests were skipped. The explicit parity gate separately passed all 5 strategy cases.
- Python syntax compilation passed for `worker.py`, `parity.py`, `smoke_test.py`, and the LEAN algorithm. The real pinned vectorbt 1.1.0 worker smoke passed and returned one candidate with one trade.
- No SQL Server, Zerodha, Azure OpenAI, paid AI, broker, or other external network call occurred. Docker and the LEAN CLI remain unavailable locally, so the manual workflow and official LEAN container were not executed. GitHub-hosted CI will first execute after these files are pushed; this local verification does not claim a GitHub Actions run or official LEAN result.

## M38 closure verification matrix

This matrix makes the verification boundary explicit for every M38 milestone. Test totals are the complete suite at that milestone; details and limitations remain in the milestone sections below.

| Milestone | Build and tests | Smoke / external validation actually executed | Integrations not executed | Broker, AI, or network calls |
| --- | --- | --- | --- | --- |
| M38.1 | Pass; 286 tests | Fake-broker direct preparation/submission | Zerodha, Azure OpenAI, LEAN, vectorbt, SQL Server | None; NuGet audit network attempts failed before the documented offline restore fallback |
| M38.2 | Pass; 291 tests | SQLite replay/concurrency and fake-broker paths | SQL Server migration, Zerodha, Azure OpenAI, LEAN, vectorbt | None |
| M38.3 | Pass; 294 tests | Durable-state tests with relational fixtures | SQL Server migration, production ledger writer, Zerodha, Azure OpenAI, LEAN, vectorbt | None |
| M38.4 | Pass; 298 tests | DB-backed selection/lineage fixtures | SQL Server migration, Zerodha, Azure OpenAI, LEAN, vectorbt | None |
| M38.5 | Pass; 306 tests | Fake broker and internal-ledger reconciliation | Production Zerodha, trusted production ledger writer, SQL Server, Azure OpenAI, LEAN, vectorbt | None |
| M38.6 | Pass; 311 tests | Canonical exchange-calendar and CLI fixtures | Zerodha, Azure OpenAI, LEAN, vectorbt, SQL Server | None |
| M38.7 | Pass; 319 tests | Executable bid/ask paper-fill fixtures | Zerodha, Azure OpenAI, LEAN, vectorbt, SQL Server | None |
| M38.8 | Pass; 325 tests | Five C#/Python parity fixtures and Python compilation | Real vectorbt portfolio runtime, LEAN, Zerodha, Azure OpenAI, SQL Server | None |
| M38.9 | Pass; 329 tests | Two-session native/independent-algorithm fixture and Python compilation | Official LEAN container/CLI, production M30 evidence, Zerodha, Azure OpenAI, SQL Server | None |
| M38.10 | Pass; 329 tests | Five parity cases, Python compilation, real vectorbt 1.1.0 smoke | GitHub-hosted workflow, official LEAN container/CLI, Zerodha, Azure OpenAI, SQL Server | None |
| M38.11 | Pass; 351 tests | 22-case research-to-fake-broker acceptance suite and sole-call-site architecture assertion | Official LEAN container/CLI, Zerodha, Azure OpenAI, SQL Server | None |

## M38.9 real LEAN validation closure

- Added `external/lean/TradingCommandCenterLean`, a separately maintained LEAN Python algorithm for `vwap-ema-trend-breakout-v1`. It imports no native strategy assembly and independently implements indicator initialization, complete signal semantics, risk sizing, next-bar entry, stop-first OHLC execution, slippage and session/end exits.
- Upgraded the M29 package to schema 2 and adapter version 2. The process boundary now requires an immutable image digest, verifies a source-hash strategy manifest, filters modern LEAN summary/auxiliary outputs, reconciles official `TotalPerformance.ClosedTrades`, and binds official-result, algorithm, strategy implementation, request and candle hashes into the portable run.
- M30 keeps its original strict tolerance policy and carries the independent-validation provenance into its sealed comparison. The two-session 5-minute fixture produced both directions and passed strict native-versus-independent-algorithm comparison with 100% matched trades and exact entry/exit timestamps.
- M32 now records `genuine-lean-validation-missing` and refuses `evidenceQualified` unless M30 contains verified official-runtime provenance. Legacy M25/M30 artifacts remain readable, but cannot manufacture new qualification authority.
- Fixed a latent portable-run precision defect exposed by the parity fixture: native final P&L evidence is now derived from the sealed trade-ledger sum, avoiding a sub-decimal difference caused by accumulating trades onto starting capital.
- Locked restore completed successfully with single-node MSBuild. Full solution build passed with 0 warnings and 0 errors. All 329 tests passed: BacktestTests 111, IntegrationTests 111, StrategyValidationTests 48, UnitTests 59. No tests were skipped. Python syntax compilation passed for the LEAN algorithm and parity harness.
- No database migration, broker call, Azure OpenAI call, SQL Server call or cloud resource was used. Docker and the LEAN CLI were absent, so no official container run, production native/LEAN result hashes, comparison hash or official verdict is claimed. The source revision is `2d61de27d79d27a7f2f58015577e969cbaa01a142e3c39bac93d469e331f7086`; the remaining four strategies fail closed until independently implemented.

## M38.8 native C# / vectorbt strategy parity closure

- Replaced the five approximate pandas signal implementations with one Decimal Python port of the native C# indicators and strategy gates. EMA, ATR, ADX, +DI, -DI, rolling volume, session VWAP, DI direction, breakout/pullback/crossing rules, previous-volume use, session boundaries, and inclusive-start/exclusive-end entry windows now follow native semantics.
- Added a normalized source SHA-256 parity manifest for all five strategies. Both the fixture harness and M26 worker reject an absent, failed or stale manifest. Schema-v2 worker evidence records the exact strategy, port version and source hash; M27 fails closed before persistence unless matching parity evidence passes. Legacy schema-v1 evidence remains readable.
- Added cross-language fixtures that export C# candles, parameters, indicators and signal expectations to the standard-library Python harness. All five strategies produced long and short cases with exact signal timestamp/direction agreement; indicator values agreed within `0.00000000000000000001`. Fixtures cover warmup, gaps, session changes and a single-gate entry-end boundary near miss.
- Added contract tests for matching/failed/mismatched parity and an integration assertion proving legacy parity-free worker evidence cannot persist an M27 sweep. Stabilized the M38 live-command fixtures with an explicit synthetic all-day calendar so they remain deterministic after India market close.
- Locked restore completed successfully. Full solution build passed with 0 warnings and 0 errors. All 325 tests passed: BacktestTests 107, IntegrationTests 111, StrategyValidationTests 48, UnitTests 59. No tests were skipped. Python syntax compilation also passed.
- No database schema change was required. Verification made no broker, Azure OpenAI, LEAN, SQL Server or network call. The configured vectorbt virtual environment was not present on this machine, so the real vectorbt 1.1.0 portfolio runtime was not smoke-tested; the exact signal/indicator port and its fail-closed manifest were executed through system Python.

## M38.7 executable-price semantics for M22

- Changed long-option paper execution to use best ask as the entry reference and best bid as the single stop/target and exit reference. LTP no longer triggers a level when an executable bid exists; adverse slippage is applied after selecting the executable reference.
- Added explicit `RequireBestBidAsk` and `AllowLastPriceFallback` quote-quality policies. The safe default rejects missing entry ask or evaluated exit bid. New schema-v2 results record the policy and each completed trade records its actual entry/exit source; explicit LTP fallback is therefore immutable evidence rather than a silent quality downgrade.
- Preserved legacy M22 hashes by making the new result/trade evidence nullable and omitting it when absent from older JSON. Existing M35 and live-chain fixture verification still passes.
- Tests prove LTP above target with bid below does not trigger, bid at target does, LTP below stop with bid above does not trigger, bid at stop does, entry uses ask, exit uses bid, slippage remains adverse, spread changes realized P&L, strict quote quality fails closed, and explicit fallback is artifacted.
- Locked restore completed successfully. Full solution build passed with 0 warnings and 0 errors. All 319 tests passed: BacktestTests 107, IntegrationTests 111, StrategyValidationTests 43, UnitTests 58. No tests were skipped.
- M38.7 adds no database table or migration and made no broker, Azure OpenAI, LEAN, vectorbt, SQL Server, or other external service call.

## M38.6 authoritative exchange calendar for M18

- Extracted M16.1 session truth into an immutable Domain `ExchangeSessionCalendar` shared by research and execution. Its canonical SHA-256 format remains based on timezone, normal hours, holidays and date-specific sessions, while the calendar ID is separately bound as evidence.
- M18 now requires an explicit calendar, uses calendar session lookup instead of weekday-only closure, intersects the exchange session with the configured entry window, and caps planned exits at both the policy mandatory-exit time and actual session close. Paper and live preparation load the same configured local calendar without network access.
- Schema-v2 `RiskDecision` records `CalendarId` and `CalendarSha256`; changing calendar rules with the same calendar ID changes the decision fingerprint. Existing serialized execution artifacts remain readable.
- Tests cover a normal weekday, weekday exchange holiday, Budget Saturday, ordinary Saturday, Muhurat shifted hours, early close, inclusive/exclusive entry boundaries, exit after a special close, and calendar-rule fingerprint changes. The CLI integration test verifies emitted calendar evidence.
- Locked restore completed successfully. Full solution build passed with 0 warnings and 0 errors. All 311 tests passed: BacktestTests 107, IntegrationTests 103, StrategyValidationTests 43, UnitTests 58. No tests were skipped.
- M38.6 adds no database table or migration and made no broker, Azure OpenAI, LEAN, vectorbt, SQL Server, or other external service call.

## M38.5 authoritative live reconciliation

- Kept `LiveReconciliationEngine` pure while adding production orchestration that obtains a fresh account snapshot from Zerodha and expected orders, fills, positions, cash and realized P&L from a durable internal ledger. Missing account identity, stale evidence, unresolved state, unmappable order/fill state, or any broker query failure stops reconciliation.
- Added schema-v2 M36 provenance for source mode, broker provider/account, broker snapshot SHA-256, internal-ledger SHA-256 and internal revision. `reconcile-live` is the broker-backed production path; `reconcile-live-file` remains available for diagnostics and can never produce DirectLive eligibility. M37 rejects diagnostic and legacy reconciliation evidence for DirectLive.
- Added immutable `InternalTradingLedgerSnapshots`, SQL-backed readers, Zerodha reconciliation mapping and a successful-M36 writer for M38.3 reconciled execution state. Persisted M23 direct orders and receipts are cross-checked against the internal ledger before M36 can pass.
- Tests with fake broker and ledger adapters prove exact match, cash mismatch, unexpected position, missing broker order, unexpected broker order, fill/status mismatch, stale broker data, and rejection of diagnostic M36 evidence by M37 DirectLive.
- Migration `20260923060024_M38AuthoritativeReconciliation` and an idempotent SSMS script were generated but not applied to the user's `Market` database. The generated EF idempotent script was inspected and contains the new table and both unique indexes.
- Locked restore completed successfully. Full solution build passed with 0 warnings and 0 errors. All 306 tests passed: BacktestTests 102, IntegrationTests 103, StrategyValidationTests 43, UnitTests 58. No tests were skipped.
- Verification used fakes and made no Zerodha, Azure OpenAI, LEAN, vectorbt, SQL Server, or other external service call. The immutable ledger reader and schema are complete, but a trusted production order-event/fill ingestion writer is not yet implemented; production M36 therefore fails closed until such a writer supplies a fresh valid ledger snapshot.

## M38.4 selection-resistant paper qualification

- Replaced production M35 session manifests with an `IPaperQualificationSessionQuery` over the inclusive M34 `QualifiedAtUtc` → operator-declared cutoff window. The command has no session-selection input and evaluates every matching durable row.
- Added schema-v2 M22 evidence and nullable durable lineage columns binding each new qualification-period session to the exact M34 qualification ID/hash, M32 certificate ID/hash, and observation start. M22 also requires its M19 execution certificate to share the M34 research run and strategy.
- Added schema-v2 M35 provenance for observation start/cutoff, first/last accepted session, discovered/accepted counts, rejected session IDs/reasons, and every included session ID/hash. Duplicate, altered, unapproved, multi-date, or lineage-mismatched evidence blocks qualification. M36 and M37 require the durable schema-v2 path; legacy M22/M35 artifacts remain readable.
- Tests prove losing durable sessions cannot be omitted, pre-M34 and other-qualification sessions do not count, duplicates are rejected, semantic artifact tampering is rejected, all valid sessions are included, and identical cutoff/state produces the same artifact SHA-256. The upgraded M22 command test verifies its stored M34 bridge.
- Migration `20260923052717_M38PaperQualificationLineage` and an idempotent SSMS script were generated but not applied to the user's `Market` database. The idempotent EF script was inspected and keeps M38.3 and M38.4 as separate ordered migrations.
- Locked restore completed successfully. Full solution build passed with 0 warnings and 0 errors. All 298 tests passed: BacktestTests 102, IntegrationTests 95, StrategyValidationTests 43, UnitTests 58. No tests were skipped.
- No Zerodha, Azure OpenAI, LEAN, vectorbt, SQL Server, or other external service call occurred during M38.4 verification.

## M38.3 durable controlled-automation state

- Removed caller-supplied completed-action and realized-loss fields from `ControlledAutomationIntent`. M37 now evaluates a single captured UTC instant against `IControlledAutomationStateProvider` evidence and emits a schema-v2 artifact containing the durable state inputs and evidence SHA-256.
- Added `ReconciledExecutionStates` with an explicit EF migration and an idempotent SSMS script. The SQL-backed provider uses India exchange-local dates and combines consumed M37 authorizations, submitted M23 direct orders, reconciled realized P&L, unresolved submissions, and active broker positions.
- Daily action limits use the greater of consumed and submitted direct actions. Realized loss uses `max(0, -realized P&L)`, so profit cannot expand configured limits. Missing, stale, wrong-date, unresolved-submission, or open-position state fails closed.
- Tests cover prior consumption, loss at threshold, profitable-day behavior, unavailable and stale state, unresolved submissions, active positions, and the 18:30 UTC India trading-date boundary. Migration/model consistency also verifies the new table.
- The migration and SQL script were generated but were not applied to the user's `Market` database during this milestone. No production writer for authoritative reconciled state exists yet; without a fresh trusted row, M37 deliberately returns `execution-state-unavailable`.
- Locked restore completed successfully. Full solution build passed with 0 warnings and 0 errors. All 294 tests passed: BacktestTests 102, IntegrationTests 91, StrategyValidationTests 43, UnitTests 58. No tests were skipped.
- No Zerodha, Azure OpenAI, LEAN, vectorbt, SQL Server, or other external service call occurred during M38.3 verification.

## M38.2 durable one-time authorization consumption

- Added the insert-only `ControlledAutomationAuthorizations` ledger with unique decision ID, SHA-256, action ID and related live-order identities plus validity/action constraints and an explicit EF migration.
- M23 now persists the prepared order, atomically reserves the single authorization action, and only then attempts broker submission. Replay, expiry and concurrency failure stop before submission; uncertain broker outcomes retain consumption permanently. Semi-live never consumes authorization.
- SQLite relational tests cover replay, concurrent reservation with exactly one winner, expiry, invalid hash and constraints. Command tests cover successful consumption, no semi-live consumption and retained consumption after a simulated uncertain broker exception.
- The SQL Server migration and idempotent SSMS script were generated but were not applied to the user's `Market` database during this milestone.
- Locked restore completed successfully with the repository's normal audit policy. Full solution build passed with 0 warnings and 0 errors. All 291 tests passed: BacktestTests 102, IntegrationTests 88, StrategyValidationTests 43, UnitTests 58. No tests were skipped.
- No Zerodha, Azure OpenAI, LEAN, vectorbt, SQL Server or other external service call occurred.

## M38.1 mandatory M37 direct-live authorization

- Direct M23 submission requires `--automation-authorization` and verifies the M37 schema, SHA-256, direct decision, one-action bound, unused marker, UTC validity, certified strategy, request/action ID and action reference before resolving `ILiveBrokerClient`.
- New direct artifacts are schema v2 and bind the M37 decision/hash plus M34 qualification, M35 paper qualification and M36 reconciliation identities. Legacy schema-v1 M23 JSON remains readable. Semi-live still submits no broker order.
- Command integration tests cover missing, blocked, observe-only, proposal-only, expired, strategy-mismatched, action-mismatched and hash-invalid authorization; all rejection paths assert zero fake-broker calls. A valid fixture reaches the existing M18/M23 gates, submits once to a fake broker and verifies the stored v2 chain.
- M38.1 makes no database change and does not yet provide durable replay prevention; that is explicitly reserved for M38.2.
- Locked restore with the repository's normal NuGet audit was attempted twice and failed with `NU1900` because this environment could not reach `https://api.nuget.org/v3/index.json`, including after a session network grant. A command-line-only `NuGetAudit=false` fallback restored the unchanged locked dependency graph; no repository audit setting was changed. Full solution build then passed with 0 warnings and 0 errors.
- All 286 tests passed: BacktestTests 102, IntegrationTests 83, StrategyValidationTests 43, UnitTests 58. No tests were skipped.
- No Zerodha, Azure OpenAI, LEAN, vectorbt, SQL Server or other network call was made during M38.1 verification.

## M16.1 exchange calendar and certified research slice

- Added full-day holiday and date-specific session parsing with special-session precedence over normal weekend closure. The supplied 2025 calendar declares Budget Saturday (09:15–15:30) and Muhurat trading (13:45–14:45). Holiday/special conflicts now fail parsing.
- The research command reads complete local dates, records every excluded observation, and sends only the certified slice to all deterministic research stages. Post-close observations on declared trading dates remain acceptable audited exclusions; candles on closed dates, duplicates, missing expected bars, and in-session off-grid bars fail certification.
- The certificate now includes a normalized calendar SHA-256, which is also bound into the dataset SHA-256. Changing holidays or session hours changes the research identity even when the calendar ID is unchanged.
- The downloaded 2025 NIFTY files contain 18,622 unique raw candles. Calendar verification produced 18,612 expected/certified candles, 10 post-close exclusions, and zero missing bars.
- Full solution build passed with 0 warnings and 0 errors. All 249 tests passed: UnitTests 56, IntegrationTests 65, StrategyValidationTests 43, BacktestTests 85. No tests skipped.

## M28 native candidate verification

- Added `verify-backtest-candidates`, which selects retained M27 candidates by research rank, re-verifies their sealed specification and declared dataset identities, and invokes the single M25 engine whose role is authoritative.
- Successful candidates retain the complete native `BacktestRun` JSON, result hash, net P&L and trade count. Expected specification or engine failures become explicit `NativeFailed` evidence.
- Candidate state changes, the sweep completion marker and one hash-bound `NativeCandidateVerificationRun` are committed in a single transaction; database uniqueness prevents later overwrite.
- A combined SQLite integration test executes M27 then M28 through fake process/engine boundaries and verifies the stored native run. Native verification does not confer research qualification or trading authority.

## M27 parameter sweep and candidate store

- Added deterministic, case-sensitive Cartesian grid expansion over existing M24 parameter names. It rejects duplicate JSON properties/values, unknown parameters, invalid top counts and more than 10,000 combinations; every combination becomes its own sealed M24 specification.
- Added `sweep-parameters` with bounded inputs, worker-role enforcement, result verification, stable score/hash ordering and atomic non-overwriting artifacts.
- Added `ParameterSweeps`, `BacktestCandidates` and `NativeCandidateVerificationRuns`, relational constraints, duplicate-prevention indexes, transactional stores, catalog/detail APIs and migration `20260916061108_M28ResearchCandidates`.
- Migration was applied to local `DESKTOP-EF1NCS7 / Market`. Live M28 smoke checks returned HTTP 200 readiness, zero stored sweeps, and the expected vectorbt/native status boundaries.

## M26 vectorbt research worker

- Added a no-shell Python process adapter with version/role checks, temporary request/result files, output bounds, cancellation, a ten-minute timeout and request/result SHA-256 evidence.
- Added vectorized screening for all five strategies using completed-bar indicators, next-row signals, session exits, stop/target distances and slippage. The worker is explicitly `researchExploration`; native calculations remain authoritative.
- Pinned vectorbt 1.1.0 and a smoke-tested Python 3.13/Windows lock file. Plotly is constrained below 7 because vectorbt 1.1.0 still registers a removed Plotly template.
- The real vectorbt worker smoke run succeeded with one candidate and 13 trades; Python compilation also passed. Full isolated-output solution build passed with 0 warnings and 0 errors. All 241 tests passed: UnitTests 48, IntegrationTests 65, StrategyValidationTests 43, BacktestTests 85. No tests skipped.
- M26–M28 made no broker, Azure OpenAI or Azure-hosting call. See `docs/M26.md`, `docs/M27.md` and `docs/M28.md`.

## M25 backtest engine abstraction

- Added `IBacktestEngine` with explicit authoritative, independent-validation and research-exploration roles, plus a portable versioned run contract that retains the sealed M24 specification and declared dataset hashes.
- Added the authoritative `native-csharp` v1 adapter. It validates registered instrument identity, reads the exact UTC half-open candle range, fingerprints consumed rows, strictly translates parameters for all five native strategies, and maps capital, costs, slippage, risk sizing and session closure into the audited deterministic engine.
- Added canonical SHA-256 sealing for portable run evidence. Validation reconciles engine identity, trade chronology, win/loss counts, gross/cost/net values, aggregate P&L and the post-trade capital ledger; tampering invalidates the result hash.
- Added `run-backtest-spec --engine native-csharp --file <sealed.json> --output <run.json>` with strict input verification, engine selection, result verification and atomic non-overwriting output. Added `/api/backtest-engines`; metadata discovery requires no SQL connection.
- Full isolated-output solution build passed with 0 warnings and 0 errors. All 235 tests passed: UnitTests 47, IntegrationTests 62, StrategyValidationTests 43, BacktestTests 83. No tests skipped. The separate output directory avoided interrupting an already-running desktop-owned `Trading.Api` process.
- M25 adds no NuGet package, table, migration, Azure resource, broker call or AI request. LEAN/vectorbt adapters, cross-engine comparison and portable-run persistence remain later work. See `docs/M25.md`.

## M24 universal backtest specification

- Added a versioned, engine-neutral `BacktestSpecification` in Application. It captures strategy parameters, instrument identity, dataset identity/hash, UTC half-open range, timeframe/calendar/time zone, capital/risk/lot limits, costs, slippage and explicit execution policies without depending on the native backtester.
- Added strict JSON handling that rejects unknown and duplicate properties, integer enum values, unsupported timeframes, non-UTC or empty ranges, invalid hashes/capital, case-colliding parameters and incompatible market-data/fill policies.
- Canonical sealing trims bounded strings, lowercases dataset hashes, sorts parameters and normalizes decimal scale before calculating SHA-256. Tests prove insertion order, whitespace and decimal scale do not change identity while semantic changes do.
- Added `seal-backtest-spec`, a checked-in sample, a draft-2020-12 JSON Schema, and `/api/backtest-specification`. The final sample command succeeded with specification SHA-256 `6257bd795e30850ec0b9f0838c76d2676c7ded3df92f2557c8bb4ac199f500d1`.
- Full isolated-output solution build passed with 0 warnings and 0 errors. All 223 tests passed: UnitTests 46, IntegrationTests 59, StrategyValidationTests 43, BacktestTests 75. No tests skipped. An already-running `Trading.Api` process held the normal output assemblies, so verification used a separate temporary output directory rather than interrupting that desktop-owned process.
- M24 adds no NuGet package, table, migration, external engine, broker call or AI request. M25 remains responsible for the common engine interface and native adapter. See `docs/M24.md`.

## M23 risk-gated semi-live and direct live entry

- Added a semi-live workflow that reads current Zerodha equity funds, net positions, today's orders and full market depth; verifies M19/M22 evidence; reapplies M18; persists a hash-bound proposal; and sends no order.
- Added a direct-live route for one BUY LIMIT MIS DAY entry. It requires the shared kill switch to be released, two independent local configuration gates, `operatorApproved=true`, and the exact CLI confirmation. All committed settings fail closed.
- Both routes require a fresh intent, account snapshot and quote; a flat account; zero broker order activity that day; exact instrument identity; a marketable price within the configured ask premium; positive cash; whole-lot sizing; and the stricter certified risk and lot limits.
- Direct mode persists a unique `Prepared` request before submission. A broker receipt changes it to `Submitted`; uncertain post-attempt outcomes return exit code 4 and require manual Zerodha reconciliation before any retry.
- Added `LiveOrders` persistence, list/detail APIs, `/live`, configuration/sample documentation, migration `20260916000000_M23RiskGatedLiveTrading`, and the equivalent SSMS script. The migration was applied to `DESKTOP-EF1NCS7 / Market`.
- Full solution build passed with 0 warnings and 0 errors. All 216 tests passed: UnitTests 42, IntegrationTests 56, StrategyValidationTests 43, BacktestTests 75. No tests skipped. The order adapter independently refuses submission while its opt-in is false, and persistence tests cover the Prepared-to-Submitted transition plus duplicate request rejection.
- Live smoke checks returned M23, HTTP 200 `Healthy` readiness, an empty live-order catalog, an engaged kill switch, direct orders disabled, and the rendered M23 page. Tests and smoke checks made no Zerodha order or Azure request.
- M23 is an intentionally narrow entry route. It does not confirm fills, place protective exits, handle partial fills, reconcile trades, recover positions, permit a second order that day, or operate unattended. See `docs/M23.md`.

## M22 deterministic paper trading

- Added a deterministic long-option paper engine over immutable M21 captures. It uses best ask/bid with adverse slippage, stop/target/planned exits, stale-tick rejection, whole-lot sizing, explicit fees and a realized cash/P&L ledger.
- Every simulated entry rebuilds point-in-time closed/open portfolio state and invokes M18. Tests prove kill-switch and daily trade-count rejections remain visible with the risk decision hash.
- The `paper-trade` command verifies the M19 certificate payload/hash/validity and M21 capture payload/hash/count, requires explicit operator approval, applies the stricter certified risk/lot/slippage/capital boundaries, and persists one hash-bound session artifact.
- Added immutable `PaperTradingSessions` persistence with certificate/capture foreign keys and configuration-level duplicate prevention, list/detail APIs, `/paper`, a sample input, migration, and SSMS script. No broker order HTTP client or endpoint exists.
- EF Core model/snapshot consistency passed. Migration `20260915230000_M22PaperTrading` was applied to `DESKTOP-EF1NCS7 / Market`; `/health/ready`, `/paper`, and `/api/paper-sessions` returned HTTP 200, `/api/status` returned M22, and the live database had zero paper sessions.
- Full solution build passed with 0 warnings and 0 errors. All 208 tests passed: UnitTests 42, IntegrationTests 48, StrategyValidationTests 43, BacktestTests 75. No tests skipped and no Zerodha or Azure call was made.
- M22 is deterministic post-capture simulation. Concurrent live paper operation, partial fills, strategy signal generation, broker reconciliation, Zerodha sandbox orders and production orders remain outside this milestone. See `docs/M22.md`.

## M21 Zerodha paper/live feed

- Added a normalized async market-feed boundary with deterministic NDJSON paper replay and read-only Zerodha sandbox/production WebSocket adapters. Production access requires the explicit `Zerodha:AllowLiveFeed=true` local opt-in.
- Added strict big-endian parsing for Kite `ltp`, `quote`, `full`, index and heartbeat frames. Token-to-exchange/symbol identity, subscription count, packet lengths, UTC replay order, price divisors and capture bounds fail closed.
- Added atomic, SHA-256-addressed JSON artifacts and immutable `MarketFeedCaptures` SQL persistence, list/detail APIs, a `/feeds` UI page, samples, User Secrets guidance and an SSMS script.
- EF Core model/snapshot consistency passed. Migration `20260915220000_M21ZerodhaMarketFeeds` was applied to `DESKTOP-EF1NCS7 / Market`; `/health/ready` returned HTTP 200 `Healthy`, `/api/status` returned M21, and the live database had zero synthetic captures.
- Full solution build passed with 0 warnings and 0 errors. All 200 tests passed: UnitTests 42, IntegrationTests 40, StrategyValidationTests 43, BacktestTests 75. No tests skipped.
- Tests made no Zerodha or Azure call. A real Zerodha connection remains credential- and market-session-dependent. M21 exposes no order operation; paper order simulation and broker execution remain later work. See `docs/M21.md`.

## M20 Astra Backtest Analyst

- Added a guarded Azure OpenAI Responses API client for the configured GPT-6 Astra deployment. Requests are explicit CLI operations, set `store: false`, enable no tools, use bounded input/output/time limits and require strict structured JSON.
- The versioned analyst instruction treats the research JSON as untrusted data and forbids recalculating indicators, P&L, sizing, ranking, qualification, certificates or risk. Its hash covers both instructions and output schema.
- Added `analyze-backtest --research-run-id <id> --output <file>`. Before any paid request, it recomputes the immutable M16 artifact hash, checks run/dataset/configuration identity and refuses a duplicate run/prompt/deployment analysis.
- Structured output must contain the matching run ID and exactly one analysis for every strategy. Missing, duplicate or unexpected strategies reject the response instead of persisting partial analysis.
- Each local artifact records deployment, returned model, provider response ID, prompt version/hash, research artifact hash, input/output token usage, structured analysis and a final SHA-256.
- Added immutable `BacktestAnalyses` persistence, catalog/detail APIs and migration `20260915210000_M20AstraBacktestAnalyst`. The migration was applied successfully to local `Market`.
- Added `/analyses` with the model authority boundary. Live `/api/status` reports M20, `/api/analyses` returns a truthful empty catalog before a real run, `/health/ready` and `/analyses` return HTTP 200.
- Full solution build passed with 0 warnings and 0 errors.
- All 192 tests passed: UnitTests 42, IntegrationTests 32, StrategyValidationTests 43, BacktestTests 75. No tests skipped. Tests use an in-memory fake Azure endpoint and make no paid model call.
- M20 analysis cannot create an M19 certificate, bypass expiry, approve M18 risk, grant human approval, connect a broker or place an order. See `docs/M20.md`.

## M19 strategy certificate

- Added deterministic schema-v1 certificate issuance from persisted M16 artifacts. The issuer never reranks: it creates one certificate per selected qualified strategy and creates none when the ranking selects none.
- Issuance recomputes the exact research artifact SHA-256, verifies catalog/run/dataset/configuration identity, requires matching strategy evidence and refuses selected entries with qualification failures.
- Each certificate binds research, strategy, instrument, timeframe, tested range, source revision, dataset/configuration/artifact hashes, M15 score components and tested risk/capital/lot/slippage/cost constraints.
- Certificates use stable IDs and hashes, default to 90 days from research creation, mark the strategy eligible for controlled paper trading, explicitly set live-trading authority false and require human approval.
- Added atomic `issue-certificates --research-run-id <id> --output-dir <directory>`. Existing files are never overwritten; database uniqueness permits one certificate per research-run/strategy pair.
- Added immutable `StrategyCertificates` SQL persistence and migration `20260915200000_M19StrategyCertificates`. The migration was applied successfully to local `Market`, and a repeated database update reports the schema current.
- Added `/api/certificates`, `/api/certificates/{id}` and `/certificates`. Live checks report M19, an empty truthful catalog before real qualification, HTTP 200 database readiness and the explicit no-live-authority UI boundary.
- Full solution build passed with 0 warnings and 0 errors.
- All 185 tests passed: UnitTests 39, IntegrationTests 28, StrategyValidationTests 43, BacktestTests 75. No tests skipped.
- M19 makes no Azure OpenAI call, broker connection or order. A certificate is research evidence for the next paper-trading stage; M18 remains mandatory for each proposed order. See `docs/M19.md`.

## M18 complete deterministic risk policy

- Added a pure point-in-time pre-trade policy with stable reason codes and culture-independent SHA-256 decision evidence. The policy uses an explicit exchange timezone and rejects future-dated state.
- Enforced qualified strategies, the Indian intraday entry/exit window, no overnight option positions, one open position, no averaging or scale-in, three trades per day, and a stop after two consecutive losses.
- Enforced ₹750 maximum risk per trade, ₹1,500 daily, ₹3,000 weekly and ₹6,000 monthly loss limits, with remaining loss capacity reducing the permitted risk before a trade is sized.
- Enforced the declared ₹3 lakh allocation: ₹1.5 lakh protected reserve, ₹1 lakh active capital, ₹30,000 strategy-testing capital and ₹20,000 buffer. Protected reserve and buffer cannot fund trades.
- Added whole-lot position sizing that rounds down from the lowest available risk/capital limit and then reevaluates the exact proposed risk and capital through the policy.
- Added `evaluate-risk --file <path>` and a checked-in sample. The documented command approved 6 lots / 150 units at ₹750 total risk and ₹15,000 required capital and emitted reproducible policy and sizing hashes.
- Added `/api/risk-policy` for the effective non-secret policy configuration. `/api/status` reports M18 and `deterministic-pre-trade-hard-gates-v1`.
- Full solution build passed with 0 warnings and 0 errors.
- All 177 tests passed: UnitTests 39, IntegrationTests 24, StrategyValidationTests 43, BacktestTests 71. No tests skipped.
- M18 adds no database table or migration. The policy consumes a point-in-time portfolio snapshot; a later execution milestone must build that snapshot from reconciled durable orders, fills and positions immediately before placing an order. See `docs/M18.md`.

## M17 real options back-testing

- Added immutable option-contract metadata and observed bid/ask/last quote storage. SQL constraints protect contract identity, strike/right/lot/tick values, positive ordered prices, volume, open interest, foreign keys and duplicate timestamps.
- Added strict per-contract CSV import with offset-aware UTC normalization, chronological rows, tick-size checks, expiry checks, overlap rejection, 10,000-row and 4 MiB limits, and atomic persistence.
- Underlying long signals select the nearest non-expired one-strike-ITM Call; short signals select the equivalent Put. Missing contracts, quotes, liquidity, open interest, acceptable spread, capital or same-session exit evidence receive explicit rejection codes.
- The simulator buys at the observed ask and sells at a later observed bid, applies adverse tick-rounded slippage, premium stop/target rules, whole-lot rupee-risk sizing, capital/lot caps, dated Indian options costs and mandatory same-session closure.
- The M17 JSON artifact records every contract/fill/cost/rejection and a culture-independent SHA-256 over all consumed underlying candles, contracts and option quotes.
- Migration `20260915160000_M17RealOptionsBacktesting` created `OptionContracts` and `OptionQuotes` in the local `Market` database. Live `/api/status` reports M17 and `/health/ready` returns HTTP 200.
- Full solution build passed with 0 warnings and 0 errors.
- All 162 tests passed: UnitTests 39, IntegrationTests 22, StrategyValidationTests 43, BacktestTests 58. No tests skipped.
- M17 uses observed quotes and does not synthesize premiums, interpolate missing data, estimate Greeks, hold overnight, call a broker, place orders or claim OOS validity by itself. See `docs/M17.md`.

## M16 research integrity closure

- M16.1 adds typed exchange calendars with full-day holidays and date-specific sessions. Special sessions override weekend/holiday closures, covering the 2025 Budget Saturday and Muhurat grids.
- Certification now returns an auditable research slice. Raw out-of-session vendor observations remain in SQL, appear as timestamped exclusions, and never reach holdout, walk-forward, robustness, regime, stress, or ranking calculations.
- The downloaded 2025 NIFTY files were checked against `data/nse-calendar-2025.csv`: 18,622 raw and unique candles, 18,612 expected/certified candles, 10 classified post-close exclusions, and zero missing expected candles.
- Added strict exchange-session certification for the requested dataset, with declared holidays, missing/duplicate/unexpected-bar findings and a canonical SHA-256 over metadata and candle values.
- Added one `run-research` command that evaluates all five strategies with comparable holdout, walk-forward, parameter-neighborhood, point-in-time regime, best-trade-removal and adverse execution evidence.
- Ranking qualification, weights and normalization scales are configuration-bound and copied into the immutable artifact.
- Added `ResearchRuns` persistence with dataset, configuration and artifact hashes. The unique dataset/configuration/source-revision index prevents accidental duplicate experiments; the report catalog returns stored metadata and exact artifact JSON.
- Added the explicit `database-update` command. Migration `20260915124000_M16ResearchIntegrity` was applied to the local `Market` database without logging its credential.
- Live `/api/status` reports M16, `/health/ready` returns HTTP 200, and `/api/reports` returns schema version 1 with zero saved runs before the first real certified experiment.
- Full solution build passed with 0 warnings and 0 errors.
- All 152 tests passed: UnitTests 37, IntegrationTests 21, StrategyValidationTests 43, BacktestTests 51. No tests skipped.
- No Azure resource, Azure OpenAI call, broker connection, option-contract selector or order authority was added. See `docs/M16.md` for the operational contract and limitations.

## M12–M15 research classification, robustness, strategies and ranking

- M12 classifies point-in-time trend, volatility and opening-gap regimes. Warm-up stays unknown, volatility baselines exclude the current ATR, and future-candle invariance is tested.
- Regime performance joins each trade at its signal timestamp and reports net results and average R by populated regime.
- M13 builds bounded reproducible Cartesian parameter neighborhoods, requires exactly one baseline, reports every scenario and calculates profitability/degradation stability without choosing an optimum.
- M14 adds Opening Range Breakout, EMA Pullback Continuation, VWAP Reclaim/Rejection and ADX Trend Continuation with distinct stable IDs, completed-candle rules, exchange-session boundaries, ATR stops and 3R defaults.
- The default strategy catalog contains all five research hypotheses. Exact fixtures verify emitted candidates, numerical evidence, parameter rejection and future-bar invariance.
- M10 and M11 SQL-backed commands can select any catalog strategy through `--strategy-id` and retain strategy #1 as the compatible default.
- M15 scores OOS edge, profit factor, drawdown, walk-forward stability, parameter robustness, regime breadth and sample size. Hard gates expose reason codes and can return zero, one or two qualified selections.
- Full solution build passed with 0 warnings and 0 errors.
- All 145 tests passed: UnitTests 34, IntegrationTests 19, StrategyValidationTests 43, BacktestTests 49. No tests skipped.
- Live application status reports M15 with all five strategy IDs, and SQL Server readiness returns HTTP 200 `Healthy`.
- No milestone in M12–M15 adds a NuGet package, migration, table, Azure call, broker connection or execution authority. See `docs/M12.md` through `docs/M15.md`.

## M11 operational walk-forward engine

- Added `run-walk-forward`, an end-to-end CLI workflow that pages a requested instrument/timeframe/range from SQL and creates consecutive out-of-sample folds.
- Rolling and anchored training modes, optional session embargoes, complete non-overlapping test windows and unused trailing-session accounting are explicit in the artifact.
- Each fold includes its exact window, selected strategy, candle counts and M9 report. A stitched combined report and profitable-fold statistics summarize all scored sessions.
- The current strategy parameters stay frozen. M11 performs no parameter search and does not expose test candles to the training selector.
- The command applies explicit risk, slippage and dated cost inputs, requires offset-aware timestamps, writes atomically and refuses to overwrite evidence.
- Full solution build passed with 0 warnings and 0 errors.
- All 129 tests passed: UnitTests 34, IntegrationTests 19, StrategyValidationTests 33, BacktestTests 43. No tests skipped.
- Live application status reports M11 and SQL Server readiness returns HTTP 200 `Healthy`.
- M11 added no NuGet package, migration, table, Azure call or execution path. See `docs/M11.md` for the command contract and limitations.

## M10 operational out-of-sample testing

- Added `run-oos`, an end-to-end CLI workflow that reads a requested instrument/timeframe/range from SQL, freezes the current strategy, runs a chronological holdout and writes a schema-versioned M9 report artifact.
- Requires explicit initial capital, rupee risk, capital cap, slippage and cost profile. Position sizing uses the registered instrument's lot size.
- Supports dated M6 Zerodha/NSE equity-intraday and equity-options profiles plus an explicitly requested `none` profile.
- SQL reads page beyond the store's 10,000-candle request cap. Input timestamps require a UTC designator or explicit offset.
- Artifact creation is atomic and refuses to overwrite an existing path.
- Full solution build passed with 0 warnings and 0 errors.
- All 126 tests passed: UnitTests 34, IntegrationTests 16, StrategyValidationTests 33, BacktestTests 43. No tests skipped.
- Live application status reports M10 and SQL Server readiness returns HTTP 200 `Healthy`.
- M10 added no NuGet package, migration, table, Azure call or execution path. See `docs/M10.md` for the command contract and limitations.

## M9 deterministic reporting and UI

- Added schema-versioned backtest reports containing M7 summary metrics, total/median/drawdown R, trade-duration statistics and exit-reason rates.
- Added monthly and yearly realized performance, entry-weekday analysis and configurable non-overlapping entry-time buckets. Undefined values remain nullable.
- Added strict camel-case JSON export with no non-finite numeric output.
- Replaced the Blazor foundation page with responsive overview and `/reports` pages. The report catalog shows a truthful empty state until named backtest-run persistence exists.
- Added `/api/reports` as a versioned empty catalog contract and mapped Razor class-library static assets in the API host.
- Full solution build passed with 0 warnings and 0 errors.
- All 123 tests passed: UnitTests 34, IntegrationTests 14, StrategyValidationTests 33, BacktestTests 42. No tests skipped.
- Live application status reports M9 and SQL Server readiness returns HTTP 200 `Healthy`.
- Browser verification confirmed the styled overview and reporting pages render correctly.
- M9 added no NuGet package, migration, table, Azure call or execution path. See `docs/M9.md` for reporting conventions and current boundaries.

## M8 chronological out-of-sample validation

- Added a fixed chronological holdout and non-overlapping walk-forward folds with configurable training, testing and embargo session counts.
- Strategy selection receives training candles only. A selected strategy can use prior context for indicator warm-up, while a gate permits candidates only inside the later test window.
- Walk-forward training can be rolling or anchored. Only complete test windows run; the result reports unused trailing sessions.
- Each fold contains its window, selected strategy ID, out-of-sample ledger and M7 metrics. Fold ledgers are rebased chronologically into one combined out-of-sample equity report.
- Full solution build passed with 0 warnings and 0 errors.
- All 116 tests passed: UnitTests 34, IntegrationTests 12, StrategyValidationTests 33, BacktestTests 37. No tests skipped.
- Live application status reports M8 and SQL Server readiness returns HTTP 200 `Healthy`.
- M8 added no NuGet package, migration, table, Azure call or execution path. See `docs/M8.md` for boundaries and limitations.

## M7 deterministic performance metrics

- Added net trade statistics, expectancy, average net R, consecutive runs, closed-equity maximum drawdown and recovery factor.
- Added a daily realized-equity series over every observed candle session, including zero-trade sessions, with annualized return, Sharpe and Sortino calculations.
- Undefined ratios remain nullable. The calculator rejects inconsistent ledgers, missing exit sessions, mixed instruments/timeframes and invalid annualization settings.
- Full solution build passed with 0 warnings and 0 errors.
- All 111 tests passed: UnitTests 34, IntegrationTests 12, StrategyValidationTests 33, BacktestTests 32. No tests skipped.
- Live application status reports M7 and SQL Server readiness returns HTTP 200 `Healthy`.
- M7 added no NuGet package, migration, table, Azure call or execution path. See `docs/M7.md` for formulas and limitations.

## M6 risk sizing and Indian-market costs

- Added deterministic sizing from allowed rupee risk divided by entry-to-stop risk, always rounded down to complete lots and capped by cash required plus an optional maximum-lot limit.
- Integrated risk sizing with M5. A candidate that cannot fund one lot is recorded as ignored; the engine never rounds up or forces a trade.
- Added dated Zerodha/NSE equity-intraday (March 1, 2026) and equity-options (April 1, 2026) profiles covering brokerage, rounded sell-side STT, exchange/IPFT, SEBI fees, buy-side stamp duty and GST.
- Each completed trade now includes component-level charge evidence. Named profiles can coexist with the M5 generic cost inputs, which appear as `Other` costs.
- Full solution build passed with 0 warnings and 0 errors.
- All 106 tests passed: UnitTests 34, IntegrationTests 12, StrategyValidationTests 33, BacktestTests 27. No tests skipped.
- Live application status reports M6 and SQL Server readiness returns HTTP 200 `Healthy`.
- M6 added no NuGet package, migration, table, Azure call or execution path. See `docs/M6.md` for formulas, dated rates and limitations.

## M5 deterministic backtesting

- Added a completed-candle backtest engine with next-observed-bar entries, one open position, same-session enforcement, configurable session liquidation and end-of-data closure.
- Stops and targets are recalculated from the slipped entry using the strategy's risk distance and reward/risk multiple. Stop wins an ambiguous candle; stop gaps fill at the open; favorable target gaps fill at the target.
- Added configurable adverse slippage, fixed per-side cost, variable turnover cost, fixed quantity and initial capital. Each trade records gross P&L, costs, net P&L and capital after trade.
- Full solution build passed with 0 warnings and 0 errors.
- All 91 tests passed: UnitTests 34, IntegrationTests 12, StrategyValidationTests 33, BacktestTests 12. No tests skipped.
- Live application status reports M5 and SQL Server readiness returns HTTP 200 `Healthy`.
- M5 added no package, migration, table or live execution path. See `docs/M5.md` for fill conventions and limitations.

## M4 strategy contract and first hypothesis

- Added a common deterministic strategy contract and `vwap-ema-trend-breakout-v1` with configurable EMA, ATR, ADX, volume, breakout, entry-window, stop and reward/risk parameters.
- Candidates include complete numerical evidence and carry no execution authority. Volume uses the prior rolling average, breakout history cannot cross an exchange-local session, and the entry window ends exclusively.
- Full solution build passed with 0 warnings and 0 errors.
- All 80 tests passed: UnitTests 34, IntegrationTests 12, StrategyValidationTests 33, BacktestTests 1. No tests skipped.
- Exact fixtures verify long and short candidates, ATR stop and 3R target arithmetic, all rejection boundaries, session reset, parameter validation and future-bar invariance.
- M4 added no package, migration, table or stored signal. Application status now reports M4. See `docs/M4.md` for the hypothesis and scope.

## M3 deterministic indicators

- Added timestamp-aligned EMA, session VWAP, Wilder ATR, Wilder ADX/+DI/-DI, rolling volume average, and a combined configurable indicator engine.
- Full solution build passed with 0 warnings and 0 errors.
- All 62 tests passed: UnitTests 34, IntegrationTests 12, StrategyValidationTests 15, BacktestTests 1. No tests skipped.
- Numerical fixtures verify EMA seeding, Wilder smoothing/warm-up positions, directional movement, flat-market division handling, volume weighting, exchange-local VWAP resets, UTC alignment, decimal precision, gaps, and invalid input rejection.
- M3 added no package, migration, table, or stored derived data. SQL Server remains the raw evidence store.
- Application status now reports M3. See `docs/M3.md` for formula conventions and boundaries.

## M2 live SQL Server completion

- Connected successfully through the application's .NET SQL provider to `DESKTOP-EF1NCS7 / Market` using the supplied SQL login.
- Verified `dbo.Instruments`, `dbo.Candles`, and `dbo.__EFMigrationsHistory`; the expected EF Core 10.0.12 migration, keys, foreign key, unique index, and all check constraints are present.
- Started the application and received HTTP 200 `Healthy` from `/health/ready`; `/api/status` returned M2.
- Registered the synthetic verification instrument and imported three candles through the actual M2 CLI. Queried SQL Server and verified all three UTC timestamps and OHLCV values.
- Repeated the same import; it correctly exited with code 2 and did not overwrite or duplicate data.
- Removed exactly the three synthetic candles and their TEST/SYNTHETIC instrument in one transaction. Final live counts: Instruments 0, Candles 0.
- The SQL credential is in ignored `src/Trading.Api/appsettings.Local.json`; `git check-ignore` confirms it is excluded. It is not in tracked configuration.

## Market SQL Server configuration

- Development settings now use the supplied Windows-authenticated connection to `DESKTOP-EF1NCS7 / Market` (application name changed to TradingCommandCenter). EF tooling reads the same Development settings unless the connection environment variable overrides them.
- `dotnet ef dbcontext info --no-build` confirmed provider SQL Server, database Market, and data source DESKTOP-EF1NCS7 without opening a connection.
- Full build passed with 0 warnings/errors. All 48 tests passed again.
- MSSQLSERVER service is running. Windows integrated authentication remained unavailable in the sandbox, but the supplied SQL login enabled live verification.
- `docs/sql/Setup-Market.sql` was applied by the user and the resulting schema was verified.

## M2

- Full solution build succeeded with 0 warnings and 0 errors.
- All 48 tests passed: UnitTests 34, IntegrationTests 12, BacktestTests 1, StrategyValidationTests 1. No tests skipped.
- Executed the actual `dotnet run ... import-candles ... --dry-run` command on the synthetic sample: exit 0, three rows, zero gaps, UTC range 2026-09-01 03:45–03:55, and source SHA-256 reported. No database connection was required.
- Relational tests verify instrument registration, successful command imports, duplicate rejection, missing-instrument rejection, tick-size validation and no writes after malformed input. These tests use SQLite; live SQL Server import remains unverified because LocalDB is not available.
- Added no NuGet packages or schema migration. The UnitTests project now references MarketData; its dependency lock file was updated.
- Existing staged M0 files and unstaged M1 work were preserved. M2 changes are unstaged/new files. No commit or publication was made.
- See `docs/M2.md` for the exact CSV contract, commands, limitations and exit codes.

## M1

- Full solution build succeeded: 0 warnings, 0 errors.
- All 25 tests passed: UnitTests 15, IntegrationTests 8, BacktestTests 1, StrategyValidationTests 1; no skipped tests.
- SQL Server `InitialMarketData` migration and snapshot generated with EF Core 10.0.12. Model consistency and idempotent SQL generation are tested. The generated SQL is in `docs/sql/M1.sql`.
- SQLite relational tests verify roundtrips, range ordering/limits, atomic rollback on duplicate bars, unique instruments, foreign keys, delete restriction and check constraints. SQLite does not replace verification against a real SQL Server instance.
- SQL Server/LocalDB was not detected; no live SQL Server migration or data writes were performed. `/health/ready` must be checked after database setup using `docs/M1.md`.
- Package restore used the same temporary official-NuGet relay/cache approach described below. EF tooling was fetched from the official NuGet package and invoked from the workspace because tool installation failed in the sandbox. No workstation-wide tool installation was made.
- Existing staged M0 files are preserved. M1 changes are unstaged/new files; no commit or remote publication was performed.

## M0

- Windows x64; SDK 10.0.401; runtime 10.0.12.
- All 15 projects restored successfully, including a subsequent locked-mode restore.
- Full Debug solution build: succeeded, 0 warnings, 0 errors.
- Tests: 7 passed, 0 failed, 0 skipped across all four test projects.
  - UnitTests: 2 options-registration/binding checks.
  - IntegrationTests: 3 successful HTTP checks for `/`, `/health`, and `/api/status` using the complete in-process host with empty SQL/AI configuration.
  - BacktestTests: 1 transitive project-dependency architecture check.
  - StrategyValidationTests: 1 transitive project-dependency architecture check.
- No SQL Server, Azure credential, or external service was needed during tests.

The Codex execution sandbox could not use the normal Windows NuGet network path or write the default profile caches. Verification used workspace-local caches, single-process MSBuild (`-m:1 -nr:false -p:UseSharedCompilation=false`), and a temporary loopback relay fetching unchanged packages over HTTPS from official NuGet endpoints. The relay also forwarded NuGet vulnerability metadata; no project audit settings were disabled. The relay is outside this repository and was stopped after verification. The committed NuGet.Config uses the normal official HTTPS feed. A normal first restore from VS Code still requires internet access.

Integration tests use ephemeral data-protection keys to avoid profile writes. The application keeps the framework's normal data-protection behavior and logs to the console. No browser interaction, live SQL connection, or Azure OpenAI call was tested or implemented in M0.
