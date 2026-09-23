using System.Text.Json;
using Trading.Api;
using Trading.Application.Backtesting;

namespace Trading.IntegrationTests;

public sealed class RobustnessSuiteCommandTests
{
    [Fact]
    public async Task Command_writes_hash_bound_suite_from_verified_native_run()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tcc-m31-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var runPath = Path.Combine(directory, "native.json");
            var settingsPath = Path.Combine(directory, "settings.json");
            var outputPath = Path.Combine(directory, "robustness.json");
            await File.WriteAllTextAsync(runPath, BacktestRunCodec.Serialize(Run()));
            await File.WriteAllTextAsync(settingsPath,
                "{\"seed\":7,\"iterations\":100,\"ruinEquityFraction\":0.8," +
                "\"additionalSlippageBasisPointsPerSide\":[0],\"costMultipliers\":[1]," +
                "\"entryDelaySeconds\":[0],\"entryDelayPenaltyBasisPointsPerSecond\":0.02," +
                "\"missedTradeProbabilities\":[0]}");
            var standardOutput = new StringWriter();
            var error = new StringWriter();

            var exit = await RobustnessSuiteCommands.RunAsync(["run-robustness-suite", "--run", runPath,
                "--settings", settingsPath, "--output", outputPath], standardOutput, error);

            Assert.Equal(0, exit);
            Assert.Empty(error.ToString());
            Assert.Contains("robustness-suite-created", standardOutput.ToString());
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
            Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(64, document.RootElement.GetProperty("artifactSha256").GetString()!.Length);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static BacktestRun Run()
    {
        var signal = new DateTime(2026, 1, 1, 4, 0, 0, DateTimeKind.Utc);
        var trade = new BacktestRunTrade("fixture", Guid.NewGuid(), BacktestRunTradeDirection.Long,
            signal, signal.AddMinutes(5), signal.AddMinutes(10), 1, 100, 95, 110, 111,
            "target", 11, 1, 10, 10_010);
        return BacktestRunCodec.Seal(new(1, "native-csharp", "1", BacktestEngineRole.Authoritative,
            new string('a', 64), new string('b', 64), new string('c', 64), 10_000, 10_010,
            10, 1, 0, [trade], [], string.Empty));
    }
}
