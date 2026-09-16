using Microsoft.Extensions.DependencyInjection;
using Trading.Api;
using Trading.Application.Backtesting;

namespace Trading.IntegrationTests;

public sealed class BacktestEngineCommandTests
{
    [Fact]
    public async Task Command_selects_engine_and_writes_verified_run_to_new_file()
    {
        var specification = Specification();
        var input = Path.Combine(Path.GetTempPath(), $"m25-{Guid.NewGuid():N}-spec.json");
        var outputPath = Path.Combine(Path.GetTempPath(), $"m25-{Guid.NewGuid():N}-run.json");
        await File.WriteAllTextAsync(input, BacktestSpecificationCodec.Serialize(specification));
        try
        {
            await using var services = new ServiceCollection().AddSingleton<IBacktestEngine>(new FakeEngine()).BuildServiceProvider();
            var output = new StringWriter(); var error = new StringWriter();
            var exit = await BacktestEngineCommands.RunAsync(["run-backtest-spec", "--engine", "fixture",
                "--file", input, "--output", outputPath], services, output, error);

            Assert.Equal(0, exit);
            Assert.Empty(error.ToString());
            Assert.Contains("backtest-run-created", output.ToString());
            Assert.Contains(specification.SpecificationSha256, await File.ReadAllTextAsync(outputPath));
        }
        finally
        {
            if (File.Exists(input)) File.Delete(input);
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task Unknown_engine_fails_without_creating_output()
    {
        var input = Path.Combine(Path.GetTempPath(), $"m25-{Guid.NewGuid():N}-spec.json");
        var outputPath = Path.Combine(Path.GetTempPath(), $"m25-{Guid.NewGuid():N}-run.json");
        await File.WriteAllTextAsync(input, BacktestSpecificationCodec.Serialize(Specification()));
        try
        {
            await using var services = new ServiceCollection().BuildServiceProvider(); var error = new StringWriter();
            var exit = await BacktestEngineCommands.RunAsync(["run-backtest-spec", "--engine", "missing",
                "--file", input, "--output", outputPath], services, TextWriter.Null, error);
            Assert.Equal(2, exit);
            Assert.Contains("Unknown backtest engine", error.ToString());
            Assert.False(File.Exists(outputPath));
        }
        finally { if (File.Exists(input)) File.Delete(input); if (File.Exists(outputPath)) File.Delete(outputPath); }
    }

    private static SealedBacktestSpecification Specification() => BacktestSpecificationCodec.Seal(new(1, "fixture",
        new(Guid.NewGuid(), "NSE", "TEST", BacktestAssetClass.Equity, "INR", 1, .05m),
        new(new DateTime(2026, 1, 1, 3, 45, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 2, 3, 45, 0, DateTimeKind.Utc), 5, "India Standard Time",
            "nse-v1", "fixture", "v1", new string('a', 64), BacktestMarketDataMode.OhlcvBars),
        new(100_000, 750, 30_000, 1),
        new(3, 0, "none", new TimeOnly(15, 25), BacktestSignalTiming.CompletedBar,
            BacktestEntryFillPolicy.NextObservedBarOpen, BacktestAmbiguousBarPolicy.StopFirst,
            BacktestEndOfDataPolicy.CloseLastObserved), new Dictionary<string, decimal>()));

    private sealed class FakeEngine : IBacktestEngine
    {
        public string EngineId => "fixture";
        public string EngineVersion => "1";
        public BacktestEngineRole Role => BacktestEngineRole.IndependentValidation;
        public Task<BacktestRun> RunAsync(SealedBacktestSpecification specification,
            CancellationToken cancellationToken = default) => Task.FromResult(BacktestRunCodec.Seal(new(1,
                EngineId, EngineVersion, Role, specification.SpecificationSha256,
                specification.Specification.Data.DatasetSha256, new string('b', 64),
                specification.Specification.Capital.InitialCapital, specification.Specification.Capital.InitialCapital,
                0, 0, 0, [], [], string.Empty)));
    }
}
