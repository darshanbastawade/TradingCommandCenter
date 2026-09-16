using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Trading.Infrastructure.Persistence;

namespace Trading.Api;

public sealed class DatabaseReadinessCheck(IServiceScopeFactory scopeFactory, IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("TradingDatabase")))
            return HealthCheckResult.Unhealthy("Database is not configured.");
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            if (!await db.Database.CanConnectAsync(cancellationToken)) return HealthCheckResult.Unhealthy("Database is unavailable.");
            if ((await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
                return HealthCheckResult.Unhealthy("Database migrations are pending.");
            // Also checks that the expected tables exist if migration history has drifted.
            _ = await db.Instruments.AsNoTracking().AnyAsync(cancellationToken);
            _ = await db.Candles.AsNoTracking().AnyAsync(cancellationToken);
            _ = await db.ResearchRuns.AsNoTracking().AnyAsync(cancellationToken);
            _ = await db.OptionContracts.AsNoTracking().AnyAsync(cancellationToken);
            _ = await db.OptionQuotes.AsNoTracking().AnyAsync(cancellationToken);
            _ = await db.StrategyCertificates.AsNoTracking().AnyAsync(cancellationToken);
            _ = await db.BacktestAnalyses.AsNoTracking().AnyAsync(cancellationToken);
            _ = await db.MarketFeedCaptures.AsNoTracking().AnyAsync(cancellationToken);
            _ = await db.PaperTradingSessions.AsNoTracking().AnyAsync(cancellationToken);
            _ = await db.LiveOrders.AsNoTracking().AnyAsync(cancellationToken);
            _ = await db.ParameterSweeps.AsNoTracking().AnyAsync(cancellationToken);
            _ = await db.BacktestCandidates.AsNoTracking().AnyAsync(cancellationToken);
            _ = await db.NativeCandidateVerificationRuns.AsNoTracking().AnyAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return HealthCheckResult.Unhealthy("Database readiness check failed."); }
    }
}
