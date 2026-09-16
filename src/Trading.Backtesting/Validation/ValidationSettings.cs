namespace Trading.Backtesting.Validation;

public sealed record HoldoutValidationSettings(
    int TrainingSessionCount,
    int EmbargoSessionCount = 0);

public sealed record WalkForwardValidationSettings(
    int TrainingSessionCount,
    int TestingSessionCount,
    int EmbargoSessionCount = 0,
    bool AnchoredTraining = false);
