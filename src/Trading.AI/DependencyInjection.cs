using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Trading.Application.AI;

namespace Trading.AI;

public static class DependencyInjection
{
    public static IServiceCollection AddTradingAIConfiguration(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AzureOpenAIOptions>()
            .Bind(configuration.GetSection(AzureOpenAIOptions.SectionName));
        services.AddSingleton<HttpClient>();
        services.AddSingleton<IBacktestAnalyst, AzureOpenAIBacktestAnalyst>();
        services.AddSingleton<IAstraResearchAnalystV2, AzureOpenAIResearchAnalystV2>();
        return services;
    }
}
