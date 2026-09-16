using Trading.Api;
using System.Runtime.CompilerServices;

namespace Trading.IntegrationTests;

public sealed class BacktestSpecificationCommandTests
{
    [Fact]
    public async Task Sample_is_sealed_to_a_new_hash_verified_document()
    {
        var repository = FindRepositoryRoot();
        var outputPath = Path.Combine(Path.GetTempPath(), $"m24-{Guid.NewGuid():N}.json");
        try
        {
            var output = new StringWriter(); var error = new StringWriter();
            var exit = await BacktestSpecificationCommands.RunAsync([
                "seal-backtest-spec", "--file", Path.Combine(repository, "samples", "backtest-specification.json"),
                "--output", outputPath], output, error);
            Assert.Equal(0, exit);
            Assert.Empty(error.ToString());
            Assert.Contains("backtest-specification-sealed", output.ToString());
            var json = await File.ReadAllTextAsync(outputPath);
            Assert.Contains("specificationSha256", json);
            Assert.Contains("vwap-ema-trend-breakout-v1", json);
        }
        finally { if (File.Exists(outputPath)) File.Delete(outputPath); }
    }

    [Fact]
    public async Task Existing_output_is_never_overwritten()
    {
        var repository = FindRepositoryRoot();
        var outputPath = Path.Combine(Path.GetTempPath(), $"m24-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(outputPath, "keep");
        try
        {
            var error = new StringWriter();
            var exit = await BacktestSpecificationCommands.RunAsync([
                "seal-backtest-spec", "--file", Path.Combine(repository, "samples", "backtest-specification.json"),
                "--output", outputPath], TextWriter.Null, error);
            Assert.Equal(2, exit);
            Assert.Equal("keep", await File.ReadAllTextAsync(outputPath));
        }
        finally { File.Delete(outputPath); }
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "TradingCommandCenter.sln"))) return directory.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }
}
