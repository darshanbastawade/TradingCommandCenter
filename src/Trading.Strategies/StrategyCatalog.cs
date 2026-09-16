using Trading.Strategies.AdxTrendContinuation;
using Trading.Strategies.Contracts;
using Trading.Strategies.EmaPullbackContinuation;
using Trading.Strategies.OpeningRangeBreakout;
using Trading.Strategies.VwapEmaTrendBreakout;
using Trading.Strategies.VwapReclaimRejection;

namespace Trading.Strategies;

public static class StrategyCatalog
{
    public static IReadOnlyList<ITradingStrategy> CreateDefaults() =>
    [
        new VwapEmaTrendBreakoutStrategy(),
        new OpeningRangeBreakoutStrategy(),
        new EmaPullbackContinuationStrategy(),
        new VwapReclaimRejectionStrategy(),
        new AdxTrendContinuationStrategy()
    ];
}
