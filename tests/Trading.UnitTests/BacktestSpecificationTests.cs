using System.Text.Json;
using Trading.Application.Backtesting;

namespace Trading.UnitTests;

public sealed class BacktestSpecificationTests
{
    [Fact]
    public void Equivalent_parameter_order_and_whitespace_produce_the_same_hash()
    {
        var first = Specification(new Dictionary<string, decimal>
        { ["slowEmaPeriod"] = 50, ["fastEmaPeriod"] = 20 });
        var second = Specification(new Dictionary<string, decimal>
        { ["fastEmaPeriod"] = 20.000m, ["slowEmaPeriod"] = 50.00m }) with
        {
            StrategyId = " vwap-ema-trend-breakout-v1 ",
            Execution = Specification().Execution with { RewardRiskMultiple = 3.000m }
        };

        var left = BacktestSpecificationCodec.Seal(first);
        var right = BacktestSpecificationCodec.Seal(second);

        Assert.Equal(left.SpecificationSha256, right.SpecificationSha256);
        Assert.Equal(["fastEmaPeriod", "slowEmaPeriod"], left.Specification.Parameters.Keys);
        Assert.True(BacktestSpecificationCodec.Verify(left));
    }

    [Fact]
    public void Semantic_change_changes_the_hash()
    {
        var baseline = BacktestSpecificationCodec.Seal(Specification());
        var changed = BacktestSpecificationCodec.Seal(Specification() with
        {
            Capital = Specification().Capital with { RiskPerTrade = 500m }
        });
        Assert.NotEqual(baseline.SpecificationSha256, changed.SpecificationSha256);
        Assert.False(BacktestSpecificationCodec.Verify(baseline with { Specification = changed.Specification }));
    }

    [Fact]
    public void Invalid_ranges_fill_combinations_and_case_colliding_parameters_fail_closed()
    {
        var baseline = Specification();
        Assert.Throws<ArgumentException>(() => BacktestSpecificationCodec.Seal(baseline with
        { Data = baseline.Data with { ToUtc = baseline.Data.FromUtc } }));
        Assert.Throws<ArgumentException>(() => BacktestSpecificationCodec.Seal(baseline with
        { Execution = baseline.Execution with { EntryFillPolicy = BacktestEntryFillPolicy.ObservedAsk } }));
        Assert.Throws<ArgumentException>(() => BacktestSpecificationCodec.Seal(baseline with
        { Parameters = new Dictionary<string, decimal> { ["Ema"] = 20, ["ema"] = 50 } }));
    }

    [Fact]
    public void Strict_json_rejects_unknown_duplicate_and_integer_enum_values()
    {
        var json = JsonSerializer.Serialize(Specification(), Json());
        Assert.Throws<JsonException>(() => BacktestSpecificationCodec.Deserialize(json.Replace(
            "\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal)));
        Assert.Throws<JsonException>(() => BacktestSpecificationCodec.Deserialize(json.Replace(
            "\"strategyId\":", "\"unexpected\":true,\"strategyId\":", StringComparison.Ordinal)));
        Assert.Throws<JsonException>(() => BacktestSpecificationCodec.Deserialize(json.Replace(
            "\"assetClass\":\"equityIndex\"", "\"assetClass\":2", StringComparison.Ordinal)));
    }

    private static BacktestSpecification Specification(IReadOnlyDictionary<string, decimal>? parameters = null) => new(
        1, "vwap-ema-trend-breakout-v1",
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "NSE", "NIFTY 50",
            BacktestAssetClass.EquityIndex, "INR", 1, .05m),
        new(new DateTime(2025, 1, 1, 3, 45, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 3, 45, 0, DateTimeKind.Utc), 5, "India Standard Time",
            "nse-v1", "local-sql", "nifty-2025-v1", new string('a', 64), BacktestMarketDataMode.OhlcvBars),
        new(100_000m, 750m, 30_000m, 5),
        new(3m, 5m, "zerodha-nse-equity-intraday-2026-03-01", new TimeOnly(15, 25),
            BacktestSignalTiming.CompletedBar, BacktestEntryFillPolicy.NextObservedBarOpen,
            BacktestAmbiguousBarPolicy.StopFirst, BacktestEndOfDataPolicy.CloseLastObserved),
        parameters ?? new Dictionary<string, decimal> { ["fastEmaPeriod"] = 20 });

    private static JsonSerializerOptions Json()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
