# Connect to the Market database

The API's committed Development connection targets `DESKTOP-EF1NCS7`, database `Market`, using Windows Authentication. The supplied SQL login is stored only in ignored `src/Trading.Api/appsettings.Local.json`, which overrides Development settings for both the application and EF tooling. The application name is `TradingCommandCenter`. `ConnectionStrings__TradingDatabase` has highest priority for EF tooling and also overrides runtime settings through standard ASP.NET Core environment configuration.

The existing application query timeout remains 15 seconds in the persistence registration; this overrides the connection string's `Command Timeout=0` for EF operations. No SQL password is stored. This is your local Development configuration; other environments still need an explicit connection.

## Create the tables in SSMS

SQL Server's `MSSQLSERVER` service is running. The schema has been created and verified in the `Market` database.

1. Open SQL Server Management Studio and connect to `DESKTOP-EF1NCS7` using Windows Authentication.
2. Open `docs/sql/Setup-Market.sql` from this repository.
3. Execute the complete script (F5).
4. Refresh **Databases → Market → Tables**.

Expected tables:

- `dbo.Instruments`
- `dbo.Candles`
- `dbo.__EFMigrationsHistory`

The script creates Market if it does not exist, applies the M1 migration and records its history, so subsequent EF migrations recognize the schema. It does not drop or overwrite tables. It stops if trading tables already exist without the expected migration history. Do not bypass that check; an existing schema needs comparison before migration. After the initial setup, apply later migrations with `database-update`; M16 adds `ResearchRuns`, M17 adds `OptionContracts` and `OptionQuotes`, M19 adds `StrategyCertificates`, and M20 adds `BacktestAnalyses`. All migrations have been applied successfully on this machine.

## Alternative: apply through EF CLI

From a normal VS Code terminal in the solution folder:

```powershell
dotnet tool restore
dotnet run --project src/Trading.Api -- database-update
```

Use either the SSMS script or EF CLI; both record the same migration. Avoid simultaneous migration attempts.

## Verify the application

```powershell
dotnet run --project src/Trading.Api --launch-profile http
```

Open http://localhost:5080/health/ready and expect `Healthy`. `/health` alone only confirms the process is running. Live readiness and the complete M2 import path were verified successfully. The temporary synthetic verification data was removed, leaving Instruments and Candles empty for real data.
