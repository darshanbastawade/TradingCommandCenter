using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Trading.Api;

namespace Trading.IntegrationTests;

public sealed class RiskPolicyCommandTests
{
    [Fact]
    public async Task Command_evaluates_file_without_database_or_wall_clock_inputs()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, """
            {
              "availableCash": 100000,
              "killSwitchEngaged": false,
              "qualifiedStrategyIds": ["qualified-v1"],
              "closedTrades": [],
              "openPositions": [],
              "request": {
                "requestId": "aaaaaaaa-1818-1818-1818-181818181818",
                "strategyId": "qualified-v1",
                "instrumentId": "bbbbbbbb-1818-1818-1818-181818181818",
                "proposedEntryUtc": "2026-09-15T04:30:00Z",
                "plannedExitUtc": "2026-09-15T09:50:00Z",
                "entryPrice": 100,
                "stopPrice": 95,
                "lotSize": 25,
                "maximumLots": null,
                "capitalPool": "Active"
              }
            }
            """);
        try
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var exit = await RiskPolicyCommands.RunAsync(["evaluate-risk", "--file", path],
                new ConfigurationBuilder().Build(), output, error);

            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, error.ToString());
            using var result = JsonDocument.Parse(output.ToString());
            Assert.True(result.RootElement.GetProperty("approved").GetBoolean());
            Assert.Equal(64, result.RootElement.GetProperty("decisionSha256").GetString()!.Length);
        }
        finally { File.Delete(path); }
    }
}
