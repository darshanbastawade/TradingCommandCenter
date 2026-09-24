using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Trading.Application.MarketData;
using Trading.Application.Research;
using Trading.Application.Execution;
using Trading.Infrastructure.Persistence;

namespace Trading.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddTradingPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<TradingDbContext>(options =>
        {
            var connection = configuration.GetConnectionString("TradingDatabase");
            if (string.IsNullOrWhiteSpace(connection))
                throw new InvalidOperationException("Configure ConnectionStrings:TradingDatabase before using persistence.");
            options.UseSqlServer(connection, sql => sql.CommandTimeout(15));
        });
        services.AddScoped<IMarketDataStore, MarketDataStore>();
        services.AddScoped<IOptionMarketDataStore, OptionMarketDataStore>();
        services.AddScoped<IResearchRunStore, ResearchRunStore>();
        services.AddScoped<IBacktestCandidateStore, BacktestCandidateStore>();
        services.AddScoped<IStrategyCertificateStore, StrategyCertificateStore>();
        services.AddScoped<IBacktestAnalysisStore, BacktestAnalysisStore>();
        services.AddScoped<IMarketFeedCaptureStore, MarketFeedCaptureStore>();
        services.AddScoped<IPaperTradingSessionStore, PaperTradingSessionStore>();
        services.AddScoped<IPaperQualificationSessionQuery, PaperTradingSessionStore>();
        services.AddScoped<ILiveOrderStore, LiveOrderStore>();
        services.AddScoped<IControlledAutomationAuthorizationStore, ControlledAutomationAuthorizationStore>();
        services.AddScoped<IControlledAutomationStateProvider, ControlledAutomationStateProvider>();
        services.AddScoped<IInternalTradingLedgerReader, InternalTradingLedgerReader>();
        services.AddScoped<IReconciledExecutionStateStore, ReconciledExecutionStateStore>();
        return services;
    }
}
