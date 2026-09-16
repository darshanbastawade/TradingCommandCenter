using Trading.Backtesting.Metrics;

namespace Trading.Backtesting.Validation;

public sealed record ValidationWindow(
    int FoldIndex,
    DateOnly TrainingStartSession,
    DateOnly TrainingEndSession,
    DateOnly TestingStartSession,
    DateOnly TestingEndSession,
    int TrainingSessionCount,
    int EmbargoSessionCount,
    int TestingSessionCount);

public sealed record ValidationFold(
    ValidationWindow Window,
    string SelectedStrategyId,
    BacktestResult OutOfSampleBacktest,
    BacktestMetrics OutOfSampleMetrics);

public sealed record WalkForwardValidationResult(
    IReadOnlyList<ValidationFold> Folds,
    BacktestResult CombinedOutOfSampleBacktest,
    BacktestMetrics CombinedOutOfSampleMetrics,
    int UnusedTrailingSessionCount)
{
    public int ProfitableFoldCount => Folds.Count(fold => fold.OutOfSampleBacktest.NetPnl > 0);
    public decimal ProfitableFoldRate => (decimal)ProfitableFoldCount / Folds.Count;
}
