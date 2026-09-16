namespace Trading.Strategies.Contracts;

public enum TradeDirection { Long = 1, Short = 2 }

public sealed record StrategyEvidence(
    decimal FastEma,
    decimal SlowEma,
    decimal SessionVwap,
    decimal Atr,
    decimal Adx,
    decimal PositiveDi,
    decimal NegativeDi,
    long Volume,
    decimal PreviousAverageVolume,
    decimal BreakoutLevel);

/// <summary>A research candidate. It is not an order and carries no execution authority.</summary>
public sealed record StrategySignal(
    string StrategyId,
    Guid InstrumentId,
    DateTime OpenTimeUtc,
    TradeDirection Direction,
    decimal EntryPrice,
    decimal StopPrice,
    decimal TargetPrice,
    decimal RiskPerUnit,
    decimal RewardRiskMultiple,
    StrategyEvidence Evidence);
