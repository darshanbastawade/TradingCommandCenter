using Trading.Backtesting.Options;

namespace Trading.Backtesting.Reporting;

public sealed record OptionsBacktestConfiguration(
    decimal InitialCapital,
    decimal AllowedRiskPerTrade,
    decimal MaximumCapitalPerTrade,
    int? MaximumLots,
    decimal PremiumStopPercent,
    decimal RewardRiskMultiple,
    long MinimumVolume,
    long MinimumOpenInterest,
    decimal MaximumSpreadBasisPoints,
    decimal SlippageBasisPointsPerSide,
    string CostProfile,
    TimeOnly SessionExitTime);

public sealed record OptionsBacktestArtifact(
    int SchemaVersion,
    DateTime CreatedAtUtc,
    string StrategyId,
    Guid UnderlyingInstrumentId,
    int TimeframeMinutes,
    DateTime FromUtc,
    DateTime ToUtc,
    string MarketDataSha256,
    int UnderlyingCandleCount,
    int OptionContractCount,
    int OptionQuoteCount,
    OptionsBacktestConfiguration Configuration,
    OptionsBacktestResult Result);
