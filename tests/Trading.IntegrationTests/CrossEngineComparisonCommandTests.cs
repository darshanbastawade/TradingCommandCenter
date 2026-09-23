using System.Text.Json;
using Trading.Api;
using Trading.Application.Backtesting;

namespace Trading.IntegrationTests;

public sealed class CrossEngineComparisonCommandTests
{
    [Fact]
    public async Task Command_writes_comparison_and_returns_nonzero_for_no_trades()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tcc-m30-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var nativePath = Path.Combine(directory, "native.json");
            var leanPath = Path.Combine(directory, "lean.json");
            var comparisonPath = Path.Combine(directory, "comparison.json");
            await File.WriteAllTextAsync(nativePath, BacktestRunCodec.Serialize(Run("native-csharp",
                BacktestEngineRole.Authoritative)));
            await File.WriteAllTextAsync(leanPath, BacktestRunCodec.Serialize(Run("lean",
                BacktestEngineRole.IndependentValidation)));

            var standardOutput = new StringWriter();
            var error = new StringWriter();
            var exit = await CrossEngineComparisonCommands.RunAsync(["compare-backtest-runs",
                "--native", nativePath, "--lean", leanPath, "--output", comparisonPath],
                standardOutput, error);

            Assert.Equal(4, exit);
            Assert.Empty(error.ToString());
            Assert.Contains("NoTrades", standardOutput.ToString());
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(comparisonPath));
            Assert.Equal("noTrades", document.RootElement.GetProperty("verdict").GetString());
            Assert.Equal(64, document.RootElement.GetProperty("comparisonSha256").GetString()!.Length);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static BacktestRun Run(string engine, BacktestEngineRole role) => BacktestRunCodec.Seal(
        new(1, engine, "1", role, new string('a', 64), new string('b', 64), new string('c', 64),
            10_000, 10_000, 0, 0, 0, [], [], string.Empty));
}
