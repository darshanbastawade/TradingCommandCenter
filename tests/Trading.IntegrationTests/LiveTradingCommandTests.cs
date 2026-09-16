using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Trading.Api;
using Trading.Execution.Live;

namespace Trading.IntegrationTests;

public sealed class LiveTradingCommandTests
{
    [Fact]
    public async Task Direct_mode_is_disabled_before_services_or_network_are_resolved()
    {
        var input = Path.Combine(Path.GetTempPath(), $"m23-{Guid.NewGuid():N}.json");
        var output = Path.Combine(Path.GetTempPath(), $"m23-{Guid.NewGuid():N}-out.json");
        try
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            await File.WriteAllTextAsync(input, JsonSerializer.Serialize(new LiveEntryIntent(Guid.NewGuid(),
                Guid.NewGuid(), 12345, "NFO", "TESTCE", new DateTime(2026, 9, 15, 3, 45, 0, DateTimeKind.Utc),
                new DateTime(2026, 9, 15, 4, 0, 0, DateTimeKind.Utc), 100, 90, 120, 25, 1, true), options));
            await using var services = new ServiceCollection().BuildServiceProvider();
            var error = new StringWriter();
            var result = await LiveTradingCommands.RunAsync(["live-order", "--mode", "direct",
                "--certificate-id", Guid.NewGuid().ToString(), "--file", input, "--output", output,
                "--confirm", "PLACE-LIVE-ORDER"], services, new ConfigurationBuilder().Build(),
                TextWriter.Null, error);
            Assert.Equal(2, result);
            Assert.Contains("disabled", error.ToString());
            Assert.False(File.Exists(output));
        }
        finally { if (File.Exists(input)) File.Delete(input); if (File.Exists(output)) File.Delete(output); }
    }
}
