using System.Text.Json;
using System.Text.Json.Serialization;
using Trading.Backtesting.Ranking;
using Trading.Backtesting.Robustness;

namespace Trading.Backtesting.Reporting;

public static class BacktestReportJson
{
    private static readonly JsonSerializerOptions CompactOptions = Options(false);
    private static readonly JsonSerializerOptions IndentedOptions = Options(true);

    public static string Serialize(BacktestReport report, bool indented = true)
    {
        ArgumentNullException.ThrowIfNull(report);
        return SerializeValue(report, indented);
    }

    public static string Serialize(OosTestArtifact artifact, bool indented = true)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return SerializeValue(artifact, indented);
    }

    public static string Serialize(WalkForwardTestArtifact artifact, bool indented = true)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return SerializeValue(artifact, indented);
    }

    public static string Serialize(ParameterRobustnessReport report, bool indented = true)
    {
        ArgumentNullException.ThrowIfNull(report);
        return SerializeValue(report, indented);
    }

    public static string Serialize(StrategyRankingResult result, bool indented = true)
    {
        ArgumentNullException.ThrowIfNull(result);
        return SerializeValue(result, indented);
    }

    public static string Serialize(ResearchIntegrityArtifact artifact, bool indented = true)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return SerializeValue(artifact, indented);
    }

    public static string Serialize(OptionsBacktestArtifact artifact, bool indented = true)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return SerializeValue(artifact, indented);
    }

    private static string SerializeValue<T>(T value, bool indented) =>
        JsonSerializer.Serialize(value, indented ? IndentedOptions : CompactOptions);

    private static JsonSerializerOptions Options(bool indented) => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = indented,
        NumberHandling = JsonNumberHandling.Strict
    };
}
