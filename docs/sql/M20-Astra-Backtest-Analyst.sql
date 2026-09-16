USE [Market];
GO

IF OBJECT_ID(N'[dbo].[BacktestAnalyses]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[BacktestAnalyses]
    (
        [Id] uniqueidentifier NOT NULL,
        [ResearchRunId] uniqueidentifier NOT NULL,
        [CreatedAtUtc] datetime2(7) NOT NULL,
        [Deployment] nvarchar(128) NOT NULL,
        [ResponseModel] nvarchar(128) NOT NULL,
        [ProviderResponseId] nvarchar(128) NOT NULL,
        [PromptVersion] nvarchar(64) NOT NULL,
        [PromptSha256] nchar(64) NOT NULL,
        [ResearchArtifactSha256] nchar(64) NOT NULL,
        [InputTokens] int NOT NULL,
        [OutputTokens] int NOT NULL,
        [AnalysisSha256] nchar(64) NOT NULL,
        [AnalysisJson] nvarchar(max) NOT NULL,
        CONSTRAINT [PK_BacktestAnalyses] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_BacktestAnalyses_ResearchRuns_ResearchRunId]
            FOREIGN KEY ([ResearchRunId]) REFERENCES [dbo].[ResearchRuns] ([Id]) ON DELETE NO ACTION
    );
    CREATE INDEX [IX_BacktestAnalyses_CreatedAtUtc] ON [dbo].[BacktestAnalyses] ([CreatedAtUtc]);
    CREATE UNIQUE INDEX [IX_BacktestAnalyses_ResearchRunId_PromptSha256_Deployment]
        ON [dbo].[BacktestAnalyses] ([ResearchRunId], [PromptSha256], [Deployment]);
END;
GO

IF NOT EXISTS (SELECT 1 FROM [dbo].[__EFMigrationsHistory]
               WHERE [MigrationId] = N'20260915210000_M20AstraBacktestAnalyst')
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260915210000_M20AstraBacktestAnalyst', N'10.0.12');
END;
GO
