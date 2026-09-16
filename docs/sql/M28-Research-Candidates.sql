BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260916061108_M28ResearchCandidates'
)
BEGIN
    CREATE TABLE [ParameterSweeps] (
        [Id] uniqueidentifier NOT NULL,
        [CreatedAtUtc] datetime2(7) NOT NULL,
        [StrategyId] nvarchar(128) NOT NULL,
        [WorkerId] nvarchar(64) NOT NULL,
        [WorkerVersion] nvarchar(64) NOT NULL,
        [BaseSpecificationSha256] nchar(64) NOT NULL,
        [DatasetSha256] nchar(64) NOT NULL,
        [GridSha256] nchar(64) NOT NULL,
        [EvaluatedCandidates] int NOT NULL,
        [StoredCandidates] int NOT NULL,
        [ArtifactSha256] nchar(64) NOT NULL,
        [ArtifactJson] nvarchar(max) NOT NULL,
        [NativeVerificationCompleted] bit NOT NULL,
        CONSTRAINT [PK_ParameterSweeps] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_ParameterSweeps_Counts] CHECK ([EvaluatedCandidates] > 0 AND [StoredCandidates] > 0 AND [StoredCandidates] <= [EvaluatedCandidates])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260916061108_M28ResearchCandidates'
)
BEGIN
    CREATE TABLE [BacktestCandidates] (
        [Id] uniqueidentifier NOT NULL,
        [ParameterSweepId] uniqueidentifier NOT NULL,
        [Rank] int NOT NULL,
        [StrategyId] nvarchar(128) NOT NULL,
        [SpecificationSha256] nchar(64) NOT NULL,
        [DatasetSha256] nchar(64) NOT NULL,
        [CandidateSpecificationJson] nvarchar(max) NOT NULL,
        [ParametersJson] nvarchar(max) NOT NULL,
        [ResearchScore] decimal(18,8) NOT NULL,
        [ResearchMetricsJson] nvarchar(max) NOT NULL,
        [ResearchEvidenceSha256] nchar(64) NOT NULL,
        [Status] nvarchar(32) NOT NULL,
        [VerifiedAtUtc] datetime2(7) NULL,
        [NativeEngineId] nvarchar(64) NOT NULL,
        [NativeEngineVersion] nvarchar(64) NOT NULL,
        [NativeResultSha256] nvarchar(64) NOT NULL,
        [NativeNetPnl] decimal(18,4) NULL,
        [NativeTradeCount] int NULL,
        [NativeRunJson] nvarchar(max) NOT NULL,
        [VerificationFailure] nvarchar(512) NOT NULL,
        CONSTRAINT [PK_BacktestCandidates] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_BacktestCandidates_Rank] CHECK ([Rank] > 0),
        CONSTRAINT [CK_BacktestCandidates_Status] CHECK ([Status] IN ('ResearchProposed', 'NativeVerified', 'NativeFailed')),
        CONSTRAINT [CK_BacktestCandidates_TradeCount] CHECK ([NativeTradeCount] IS NULL OR [NativeTradeCount] >= 0),
        CONSTRAINT [FK_BacktestCandidates_ParameterSweeps_ParameterSweepId] FOREIGN KEY ([ParameterSweepId]) REFERENCES [ParameterSweeps] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260916061108_M28ResearchCandidates'
)
BEGIN
    CREATE TABLE [NativeCandidateVerificationRuns] (
        [Id] uniqueidentifier NOT NULL,
        [ParameterSweepId] uniqueidentifier NOT NULL,
        [CreatedAtUtc] datetime2(7) NOT NULL,
        [EngineId] nvarchar(64) NOT NULL,
        [EngineVersion] nvarchar(64) NOT NULL,
        [RequestedCandidates] int NOT NULL,
        [VerifiedCandidates] int NOT NULL,
        [FailedCandidates] int NOT NULL,
        [ArtifactSha256] nchar(64) NOT NULL,
        [ArtifactJson] nvarchar(max) NOT NULL,
        CONSTRAINT [PK_NativeCandidateVerificationRuns] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_NativeCandidateVerificationRuns_Counts] CHECK ([RequestedCandidates] > 0 AND [VerifiedCandidates] >= 0 AND [FailedCandidates] >= 0 AND [VerifiedCandidates] + [FailedCandidates] = [RequestedCandidates]),
        CONSTRAINT [FK_NativeCandidateVerificationRuns_ParameterSweeps_ParameterSweepId] FOREIGN KEY ([ParameterSweepId]) REFERENCES [ParameterSweeps] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260916061108_M28ResearchCandidates'
)
BEGIN
    CREATE UNIQUE INDEX [IX_BacktestCandidates_ParameterSweepId_Rank] ON [BacktestCandidates] ([ParameterSweepId], [Rank]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260916061108_M28ResearchCandidates'
)
BEGIN
    CREATE UNIQUE INDEX [IX_BacktestCandidates_ParameterSweepId_SpecificationSha256] ON [BacktestCandidates] ([ParameterSweepId], [SpecificationSha256]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260916061108_M28ResearchCandidates'
)
BEGIN
    CREATE UNIQUE INDEX [IX_NativeCandidateVerificationRuns_ParameterSweepId] ON [NativeCandidateVerificationRuns] ([ParameterSweepId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260916061108_M28ResearchCandidates'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ParameterSweeps_BaseSpecificationSha256_GridSha256_WorkerId_WorkerVersion] ON [ParameterSweeps] ([BaseSpecificationSha256], [GridSha256], [WorkerId], [WorkerVersion]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260916061108_M28ResearchCandidates'
)
BEGIN
    CREATE INDEX [IX_ParameterSweeps_CreatedAtUtc] ON [ParameterSweeps] ([CreatedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260916061108_M28ResearchCandidates'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260916061108_M28ResearchCandidates', N'10.0.12');
END;

COMMIT;
GO

