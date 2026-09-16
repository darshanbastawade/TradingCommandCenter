using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Trading.IntegrationTests;

public sealed class HostSmokeTests
{
    [Fact]
    public async Task Readiness_is_unhealthy_without_database_configuration()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services => services.AddDataProtection().UseEphemeralDataProtectionProvider());
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:TradingDatabase"] = "" }));
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/ready");
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData("/health", "Healthy")]
    [InlineData("/api/status", "\"milestone\":\"M28\"")]
    [InlineData("/api/backtest-specification", "\"schemaVersion\":1")]
    [InlineData("/api/backtest-engines", "native-csharp")]
    [InlineData("/api/parameter-sweeps", "\"schemaVersion\":1")]
    [InlineData("/api/risk-policy", "india-intraday-options-conservative-v1")]
    [InlineData("/api/reports", "\"schemaVersion\":1")]
    [InlineData("/api/certificates", "\"schemaVersion\":1")]
    [InlineData("/api/analyses", "\"schemaVersion\":1")]
    [InlineData("/api/feed-captures", "\"schemaVersion\":1")]
    [InlineData("/api/paper-sessions", "\"schemaVersion\":1")]
    [InlineData("/api/live-orders", "\"schemaVersion\":1")]
    [InlineData("/", "Trading Command Center")]
    [InlineData("/reports", "No certified reports in Market yet")]
    [InlineData("/candidates", "Parameter candidates")]
    [InlineData("/certificates", "Strategy certificates")]
    [InlineData("/analyses", "Backtest analyses")]
    [InlineData("/feeds", "Paper and Zerodha feed captures")]
    [InlineData("/paper", "Deterministic paper sessions")]
    [InlineData("/live", "Risk-gated semi-live and direct entry")]
    public async Task Host_runs_without_external_services(string path, string expected)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            // Tests must not write keys to the developer's profile.
            builder.ConfigureServices(services => services.AddDataProtection().UseEphemeralDataProtectionProvider());
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:TradingDatabase"] = "",
                    ["AzureOpenAI:Endpoint"] = "",
                    ["AzureOpenAI:Deployment"] = "",
                    ["AzureOpenAI:ApiKey"] = ""
                }));
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        Assert.Contains(expected, await response.Content.ReadAsStringAsync());
    }
}
