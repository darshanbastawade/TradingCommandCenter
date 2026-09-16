# Trading Command Center — M23

SQL Server is configured for `DESKTOP-EF1NCS7 / Market`. See [M23 semi-live/direct entry](docs/M23.md), [M22 deterministic paper trading](docs/M22.md), [M21 Zerodha paper/live feed](docs/M21.md), [M20 Astra Backtest Analyst](docs/M20.md), [M19 strategy certificates](docs/M19.md), and [M18 deterministic risk policy](docs/M18.md). Local-first modular monolith. C# owns deterministic calculations and hard risk gates; SQL stores immutable evidence and AI analysis; Azure OpenAI explains research results; the operator retains final authority.

## Start in VS Code

Open this folder in VS Code (`code .` from this directory). Install the recommended Microsoft C# Dev Kit extension when prompted. No Visual Studio installation is required.

Prerequisites: .NET SDK 10.0.401 (or a later 10.0.4xx patch), Git, and internet access for the first NuGet restore.

```powershell
dotnet restore TradingCommandCenter.sln --locked-mode
dotnet build TradingCommandCenter.sln --no-restore
dotnet test TradingCommandCenter.sln --no-build
dotnet run --project src/Trading.Api --launch-profile http
```

Open http://localhost:5080 for the overview, `/reports` for research, `/certificates` for qualification evidence, `/analyses` for the Astra workflow, `/feeds` for market captures, `/paper` for simulation, and `/live` for the M23 boundary. `/health` checks application liveness only; `/api/status` reports the milestone. Stop with Ctrl+C. In VS Code, Ctrl+Shift+B builds and F5 starts the debugger. The `test` task runs all four test projects.

### Why .NET 10 instead of .NET 8?

On September 14, 2026, this machine has SDK 10.0.401 and runtime 10.0.12 installed. .NET 8 support ends November 10, 2026; .NET 10 LTS is supported until November 14, 2028. This avoids an immediate framework migration for a new project. See [Microsoft's support policy](https://dotnet.microsoft.com/en-us/platform/support/policy). `global.json` pins the SDK feature band; all projects target `net10.0` centrally.

## Project boundaries

| Project | Responsibility reserved for later milestones | Direct project references |
| --- | --- | --- |
| Domain | Entities, value objects and invariants | None |
| Application | Use cases and external-service contracts | Domain |
| Infrastructure | SQL Server persistence and adapters | Application |
| MarketData | Underlying candle and observed option-quote imports | Application |
| Strategies | Strategy contracts, evidence and deterministic hypotheses | Domain |
| Backtesting | Deterministic fills, Indian costs, metrics, validation, ranking and certificates | Application, Strategies, Risk, MarketData |
| Risk | Capital pools, loss limits, pre-trade hard gates and whole-lot sizing | Domain |
| Execution | Replay, read-only feeds, paper fills, and guarded Zerodha LIMIT-entry adapter | Application, Risk |
| AI | Guarded Azure OpenAI Responses client and structured backtest analyst | Application |
| Api | Composition root and sole executable host | All modules |
| Web | Responsive Blazor reporting interface served by Api | None |

All names carry the `Trading.` prefix. Dependencies flow inward. Domain has no NuGet packages or project dependencies. Web is a presentation module in the same process, not a second service. External-service interfaces belong in Application when they become necessary. The strategy contract lives with the deterministic Strategies module.

`UnitTests` verifies configuration, market-data parsing and domain invariants. `IntegrationTests` checks the host, readiness, relational persistence and migration consistency. `BacktestTests` verifies execution, costs, risk sizing and metrics. `StrategyValidationTests` verifies deterministic indicator and strategy behavior; passing fixtures do not establish a profitable edge.

## Baseline packages

Versions are centralized in `Directory.Packages.props`; each project commits `packages.lock.json` for repeatable restores.

- Infrastructure: EF Core SQL Server and EF Core Design (private tooling dependency). DbContext, market-data, immutable research, certificate and AI-analysis stores, and migrations are implemented. Database updates are an explicit CLI step.
- AI: Microsoft options binding plus a guarded `HttpClient` Responses API adapter. M20 uses no third-party agent framework and enables no model tools.
- Execution: framework WebSocket support plus an explicit Kite binary parser. M21 adds no broker SDK or NuGet package.
- M22 adds a deterministic paper fill/ledger engine and no package or broker order client.
- M23 uses framework `HttpClient` for Zerodha funds, positions, day orders, quotes and guarded LIMIT buys. The shared kill switch defaults to engaged and both direct-order gates default to false.
- Tests: Microsoft.NET.Test.Sdk, xUnit, Visual Studio test adapter, Coverlet collector; integration tests also use ASP.NET Core MVC Testing, and unit tests use the dependency-injection container to verify options registration.
- ASP.NET Core and Blazor use the shared framework. No extra UI framework is needed.

When intentionally updating a dependency, edit the central version, run `dotnet restore --force-evaluate`, run build/tests, and commit the changed lock files. Nullable checking and warnings-as-errors are enabled solution-wide.

## Configuration and secrets

The API owns configuration. Default ASP.NET Core ordering applies: appsettings.json → environment-specific appsettings → User Secrets in Development → environment variables → command-line arguments (later values win).

The committed AzureOpenAI section has empty Endpoint and Deployment values. ApiKey exists only in the options type; keep its real value outside Git. A key is needed only when you intentionally run `analyze-backtest`. The deployment is your Azure deployment name for GPT-6 Astra, not a hard-coded model name.

When ready, run these commands locally from the repository root, replacing the example values:

```powershell
dotnet user-secrets set "AzureOpenAI:Endpoint" "https://YOUR-RESOURCE.openai.azure.com/" --project src/Trading.Api
dotnet user-secrets set "AzureOpenAI:Deployment" "YOUR-DEPLOYMENT-NAME" --project src/Trading.Api
dotnet user-secrets set "AzureOpenAI:ApiKey" "YOUR-KEY" --project src/Trading.Api
```

The stable UserSecretsId is already in Trading.Api.csproj; do not run `init` again. User Secrets are development-only local storage and are not encrypted. The commands above are instructions, not commands executed by this scaffold. No credentials were created or read.

Alternatively, use the standard .NET environment variable names:

```text
AzureOpenAI__Endpoint
AzureOpenAI__Deployment
AzureOpenAI__ApiKey
ConnectionStrings__TradingDatabase
Zerodha__ApiKey
Zerodha__AccessToken
Zerodha__UserId
Zerodha__AllowLiveFeed
Zerodha__AllowLiveOrders
LiveTrading__KillSwitchEngaged
LiveTrading__AllowDirectOrders
```

Use double underscores for nested configuration. Earlier proposed names such as `AZURE_OPENAI_API_KEY` are not mapped automatically by this scaffold. `.env` files are ignored by Git but are not automatically loaded. Never put secrets in Web, launchSettings.json, VS Code settings, logs, or tracked appsettings. This follows [OpenAI's guidance to keep API keys outside source code](https://developers.openai.com/api/docs/guides/production-best-practices).

Development appsettings uses the supplied Windows-authenticated SQL Server connection to `DESKTOP-EF1NCS7 / Market`. The EF design-time factory reads that same setting unless `ConnectionStrings__TradingDatabase` overrides it. Follow docs/Market-setup.md to apply the schema. These connection settings are local-development settings.

## Git and scope

A standalone repository is initialized on `main`. No remote is configured and nothing is published. `.gitignore` excludes builds, test results, local settings, keys and secrets; lock files and VS Code configuration are tracked.

The implementation through M23 is local and uncommitted. This machine has no configured Git author, so no initial commit was made. To create it, set your identity locally and commit:

```powershell
git config user.name "YOUR NAME"
git config user.email "YOUR EMAIL"
git add .
git commit -m "Add trading command center through M23 guarded live entry"
```

M23 adds semi-live proposals and an independently gated direct Zerodha LIMIT-buy route. It verifies certificate and aggregate paper evidence, requires fresh broker state and depth, refuses non-flat or already-active trading days, reapplies M18, and persists an idempotent prepared record before submission. The shared kill switch is engaged and both direct gates are disabled in committed configuration. Fill/exit reconciliation and unattended live strategy operation remain future work.
