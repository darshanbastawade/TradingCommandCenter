USE [Market];
GO

IF OBJECT_ID(N'[dbo].[ReconciledExecutionStates]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[ReconciledExecutionStates]
    (
        [Id] uniqueidentifier NOT NULL,
        [AsOfUtc] datetime2(7) NOT NULL,
        [ExchangeTradingDate] date NOT NULL,
        [RealizedPnlToday] decimal(18,4) NOT NULL,
        [UnresolvedBrokerSubmissions] int NOT NULL,
        [ActiveOpenBrokerPositions] int NOT NULL,
        [ReconciliationId] uniqueidentifier NOT NULL,
        [ReconciliationSha256] nchar(64) NOT NULL,
        [SourceRevision] nvarchar(128) NOT NULL,
        [Authoritative] bit NOT NULL,
        CONSTRAINT [PK_ReconciledExecutionStates] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_ReconciledExecutionStates_Counts]
            CHECK ([UnresolvedBrokerSubmissions] >= 0 AND [ActiveOpenBrokerPositions] >= 0)
    );
    CREATE UNIQUE INDEX [IX_ReconciledExecutionStates_ExchangeTradingDate_AsOfUtc]
        ON [dbo].[ReconciledExecutionStates] ([ExchangeTradingDate], [AsOfUtc]);
    CREATE UNIQUE INDEX [IX_ReconciledExecutionStates_ReconciliationId]
        ON [dbo].[ReconciledExecutionStates] ([ReconciliationId]);
END;
GO

IF NOT EXISTS (SELECT 1 FROM [dbo].[__EFMigrationsHistory]
               WHERE [MigrationId] = N'20260923050053_M38DurableExecutionState')
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260923050053_M38DurableExecutionState', N'10.0.12');
END;
GO
