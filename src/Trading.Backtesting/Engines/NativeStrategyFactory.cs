using Trading.Strategies.AdxTrendContinuation;
using Trading.Strategies.Contracts;
using Trading.Strategies.EmaPullbackContinuation;
using Trading.Strategies.OpeningRangeBreakout;
using Trading.Strategies.VwapEmaTrendBreakout;
using Trading.Strategies.VwapReclaimRejection;

namespace Trading.Backtesting.Engines;

internal static class NativeStrategyFactory
{
    public static ITradingStrategy Create(string strategyId, IReadOnlyDictionary<string, decimal> values,
        decimal executionRewardRisk)
    {
        var parameters = new Parameters(values);
        ITradingStrategy strategy = strategyId switch
        {
            VwapEmaTrendBreakoutStrategy.StrategyId => new VwapEmaTrendBreakoutStrategy(new()
            {
                FastEmaPeriod = parameters.Integer("fastEmaPeriod"),
                SlowEmaPeriod = parameters.Integer("slowEmaPeriod"),
                AtrPeriod = parameters.Integer("atrPeriod"),
                AdxPeriod = parameters.Integer("adxPeriod"),
                VolumeAveragePeriod = parameters.Integer("volumeAveragePeriod"),
                BreakoutLookbackBars = parameters.Integer("breakoutLookbackBars"),
                MinimumAdx = parameters.Decimal("minimumAdx"),
                VolumeMultiplier = parameters.Decimal("volumeMultiplier"),
                AtrStopMultiple = parameters.Decimal("atrStopMultiple"),
                RewardRiskMultiple = parameters.Decimal("rewardRiskMultiple"),
                EntryWindowStart = parameters.MinuteOfDay("entryWindowStartMinuteOfDay"),
                EntryWindowEnd = parameters.MinuteOfDay("entryWindowEndMinuteOfDay")
            }),
            OpeningRangeBreakoutStrategy.StrategyId => new OpeningRangeBreakoutStrategy(new()
            {
                FastEmaPeriod = parameters.Integer("fastEmaPeriod"),
                SlowEmaPeriod = parameters.Integer("slowEmaPeriod"),
                AtrPeriod = parameters.Integer("atrPeriod"),
                AdxPeriod = parameters.Integer("adxPeriod"),
                VolumeAveragePeriod = parameters.Integer("volumeAveragePeriod"),
                OpeningRangeBars = parameters.Integer("openingRangeBars"),
                VolumeMultiplier = parameters.Decimal("volumeMultiplier"),
                AtrStopMultiple = parameters.Decimal("atrStopMultiple"),
                RewardRiskMultiple = parameters.Decimal("rewardRiskMultiple"),
                EntryWindowStart = parameters.MinuteOfDay("entryWindowStartMinuteOfDay"),
                EntryWindowEnd = parameters.MinuteOfDay("entryWindowEndMinuteOfDay")
            }),
            EmaPullbackContinuationStrategy.StrategyId => new EmaPullbackContinuationStrategy(new()
            {
                FastEmaPeriod = parameters.Integer("fastEmaPeriod"),
                SlowEmaPeriod = parameters.Integer("slowEmaPeriod"),
                AtrPeriod = parameters.Integer("atrPeriod"),
                AdxPeriod = parameters.Integer("adxPeriod"),
                VolumeAveragePeriod = parameters.Integer("volumeAveragePeriod"),
                MinimumAdx = parameters.Decimal("minimumAdx"),
                VolumeMultiplier = parameters.Decimal("volumeMultiplier"),
                AtrStopMultiple = parameters.Decimal("atrStopMultiple"),
                RewardRiskMultiple = parameters.Decimal("rewardRiskMultiple"),
                EntryWindowStart = parameters.MinuteOfDay("entryWindowStartMinuteOfDay"),
                EntryWindowEnd = parameters.MinuteOfDay("entryWindowEndMinuteOfDay")
            }),
            VwapReclaimRejectionStrategy.StrategyId => new VwapReclaimRejectionStrategy(new()
            {
                FastEmaPeriod = parameters.Integer("fastEmaPeriod"),
                SlowEmaPeriod = parameters.Integer("slowEmaPeriod"),
                AtrPeriod = parameters.Integer("atrPeriod"),
                AdxPeriod = parameters.Integer("adxPeriod"),
                VolumeAveragePeriod = parameters.Integer("volumeAveragePeriod"),
                MinimumAdx = parameters.Decimal("minimumAdx"),
                VolumeMultiplier = parameters.Decimal("volumeMultiplier"),
                AtrStopMultiple = parameters.Decimal("atrStopMultiple"),
                RewardRiskMultiple = parameters.Decimal("rewardRiskMultiple"),
                EntryWindowStart = parameters.MinuteOfDay("entryWindowStartMinuteOfDay"),
                EntryWindowEnd = parameters.MinuteOfDay("entryWindowEndMinuteOfDay")
            }),
            AdxTrendContinuationStrategy.StrategyId => new AdxTrendContinuationStrategy(new()
            {
                FastEmaPeriod = parameters.Integer("fastEmaPeriod"),
                SlowEmaPeriod = parameters.Integer("slowEmaPeriod"),
                AtrPeriod = parameters.Integer("atrPeriod"),
                AdxPeriod = parameters.Integer("adxPeriod"),
                VolumeAveragePeriod = parameters.Integer("volumeAveragePeriod"),
                MinimumAdx = parameters.Decimal("minimumAdx"),
                AtrStopMultiple = parameters.Decimal("atrStopMultiple"),
                RewardRiskMultiple = parameters.Decimal("rewardRiskMultiple"),
                EntryWindowStart = parameters.MinuteOfDay("entryWindowStartMinuteOfDay"),
                EntryWindowEnd = parameters.MinuteOfDay("entryWindowEndMinuteOfDay")
            }),
            _ => throw new ArgumentException($"Native engine does not support strategy '{strategyId}'.", nameof(strategyId))
        };
        parameters.Complete();
        if (parameters.Decimal("rewardRiskMultiple", markUsed: false) != executionRewardRisk)
            throw new ArgumentException("Strategy and execution reward/risk values must match.", nameof(values));
        return strategy;
    }

    private sealed class Parameters(IReadOnlyDictionary<string, decimal> values)
    {
        private readonly HashSet<string> used = new(StringComparer.Ordinal);

        public decimal Decimal(string name, bool markUsed = true)
        {
            if (!values.TryGetValue(name, out var value))
                throw new ArgumentException($"Strategy parameter '{name}' is required.", nameof(values));
            if (markUsed) used.Add(name);
            return value;
        }

        public int Integer(string name)
        {
            var value = Decimal(name);
            if (value != decimal.Truncate(value) || value is < int.MinValue or > int.MaxValue)
                throw new ArgumentException($"Strategy parameter '{name}' must be an integer.", nameof(values));
            return decimal.ToInt32(value);
        }

        public TimeOnly MinuteOfDay(string name)
        {
            var minutes = Integer(name);
            if (minutes is < 0 or >= 24 * 60)
                throw new ArgumentException($"Strategy parameter '{name}' must be 0-1439.", nameof(values));
            return TimeOnly.MinValue.AddMinutes(minutes);
        }

        public void Complete()
        {
            var unexpected = values.Keys.Where(name => !used.Contains(name)).Order(StringComparer.Ordinal).ToArray();
            if (unexpected.Length > 0)
                throw new ArgumentException($"Native strategy has unexpected parameters: {string.Join(',', unexpected)}.", nameof(values));
        }
    }
}
