using Trading.Application.Backtesting;

namespace Trading.ExternalValidation.Lean;

/// <summary>
/// Rejects specifications the paired LEAN project cannot translate without changing semantics.
/// The external algorithm owns its strategy calculations; this does not call native strategy code.
/// </summary>
public static class LeanStrategyTranslator
{
    private static readonly string[] Common =
    [
        "fastEmaPeriod", "slowEmaPeriod", "atrPeriod", "adxPeriod",
        "volumeAveragePeriod", "atrStopMultiple", "rewardRiskMultiple",
        "entryWindowStartMinuteOfDay", "entryWindowEndMinuteOfDay"
    ];

    public static void Validate(BacktestSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        if (specification.Execution.CostProfileId != "none")
            throw new NotSupportedException("LEAN adapter v1 supports the no-cost profile only.");
        string[] extra = specification.StrategyId switch
        {
            "vwap-ema-trend-breakout-v1" =>
                ["breakoutLookbackBars", "minimumAdx", "volumeMultiplier"],
            "opening-range-breakout-v1" => ["openingRangeBars", "volumeMultiplier"],
            "ema-pullback-continuation-v1" or "vwap-reclaim-rejection-v1" =>
                ["minimumAdx", "volumeMultiplier"],
            "adx-trend-continuation-v1" => ["minimumAdx"],
            _ => throw new NotSupportedException(
                $"LEAN adapter does not support strategy '{specification.StrategyId}'.")
        };
        var expected = Common.Concat(extra).ToHashSet(StringComparer.Ordinal);
        if (specification.Parameters.Count != expected.Count ||
            specification.Parameters.Keys.Any(key => !expected.Contains(key)))
            throw new ArgumentException("LEAN strategy parameters must match the selected strategy exactly.");
        foreach (var name in new[] { "fastEmaPeriod", "slowEmaPeriod", "atrPeriod", "adxPeriod",
                     "volumeAveragePeriod", "entryWindowStartMinuteOfDay", "entryWindowEndMinuteOfDay" }
            .Concat(extra.Where(value => value is "breakoutLookbackBars" or "openingRangeBars")))
        {
            var value = specification.Parameters[name];
            if (value != decimal.Truncate(value) || value < 0 || value > int.MaxValue)
                throw new ArgumentException($"LEAN strategy parameter '{name}' must be a non-negative integer.");
        }
        if (specification.Parameters["fastEmaPeriod"] < 1 ||
            specification.Parameters["slowEmaPeriod"] <= specification.Parameters["fastEmaPeriod"] ||
            specification.Parameters["atrPeriod"] < 1 ||
            specification.Parameters["adxPeriod"] < 2 ||
            specification.Parameters["volumeAveragePeriod"] < 1 ||
            specification.Parameters["entryWindowStartMinuteOfDay"] >=
                specification.Parameters["entryWindowEndMinuteOfDay"] ||
            specification.Parameters["entryWindowEndMinuteOfDay"] >= 1440 ||
            specification.Parameters["atrStopMultiple"] <= 0 ||
            specification.Parameters["rewardRiskMultiple"] !=
                specification.Execution.RewardRiskMultiple ||
            extra.Any(name =>
                (name is "volumeMultiplier" or "openingRangeBars" or "breakoutLookbackBars") &&
                specification.Parameters[name] <= 0) ||
            extra.Contains("minimumAdx") && specification.Parameters["minimumAdx"] is < 0 or > 100)
            throw new ArgumentException("LEAN strategy parameter values are invalid or conflict with execution settings.");
    }
}
