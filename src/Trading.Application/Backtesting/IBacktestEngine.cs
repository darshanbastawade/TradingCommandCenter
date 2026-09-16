namespace Trading.Application.Backtesting;

public enum BacktestEngineRole
{
    Authoritative = 1,
    IndependentValidation = 2,
    ResearchExploration = 3
}

public enum BacktestRunTradeDirection { Long = 1, Short = 2 }

public sealed record BacktestEngineDescriptor(
    string EngineId,
    string EngineVersion,
    BacktestEngineRole Role);

public sealed record BacktestRunTrade(
    string StrategyId,
    Guid InstrumentId,
    BacktestRunTradeDirection Direction,
    DateTime SignalTimeUtc,
    DateTime EntryTimeUtc,
    DateTime ExitTimeUtc,
    int Quantity,
    decimal EntryPrice,
    decimal StopPrice,
    decimal TargetPrice,
    decimal ExitPrice,
    string ExitReason,
    decimal GrossPnl,
    decimal Costs,
    decimal NetPnl,
    decimal CapitalAfterTrade);

public sealed record BacktestRunIgnoredCandidate(DateTime SignalTimeUtc, string Reason);

public sealed record BacktestRun(
    int SchemaVersion,
    string EngineId,
    string EngineVersion,
    BacktestEngineRole EngineRole,
    string SpecificationSha256,
    string DeclaredDatasetSha256,
    string ConsumedMarketDataSha256,
    decimal InitialCapital,
    decimal FinalCapital,
    decimal NetPnl,
    int WinningTrades,
    int LosingTrades,
    IReadOnlyList<BacktestRunTrade> Trades,
    IReadOnlyList<BacktestRunIgnoredCandidate> IgnoredCandidates,
    string ResultSha256);

public interface IBacktestEngine
{
    string EngineId { get; }
    string EngineVersion { get; }
    BacktestEngineRole Role { get; }

    Task<BacktestRun> RunAsync(SealedBacktestSpecification specification,
        CancellationToken cancellationToken = default);
}
