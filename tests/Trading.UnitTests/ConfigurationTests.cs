using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Trading.AI;
using Trading.Application.AI;

namespace Trading.UnitTests;

public sealed class ConfigurationTests
{
    [Fact]
    public void Empty_configuration_resolves_without_credentials()
    {
        using var services = new ServiceCollection()
            .AddTradingAIConfiguration(new ConfigurationBuilder().Build())
            .BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value;
        Assert.Empty(options.ApiKey);
        Assert.Empty(options.Endpoint);
        Assert.Empty(options.Deployment);
        Assert.IsType<AzureOpenAIResearchAnalystV2>(
            services.GetRequiredService<IAstraResearchAnalystV2>());
    }

    [Fact]
    public void Configuration_section_binds_to_AI_options()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureOpenAI:Endpoint"] = "https://example.openai.azure.com/",
            ["AzureOpenAI:Deployment"] = "test-deployment",
            ["AzureOpenAI:ApiKey"] = "test-only-placeholder"
        }).Build();
        using var services = new ServiceCollection().AddTradingAIConfiguration(config).BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value;
        Assert.Equal("https://example.openai.azure.com/", options.Endpoint);
        Assert.Equal("test-deployment", options.Deployment);
        Assert.Equal("test-only-placeholder", options.ApiKey);
    }
}
