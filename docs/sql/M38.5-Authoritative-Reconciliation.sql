USE [Market];
GO

IF OBJECT_ID(N'[dbo].[InternalTradingLedgerSnapshots]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[InternalTradingLedgerSnapshots]
    (
        [Id] uniqueidentifier NOT NULL,
        [AsOfUtc] datetime2(7) NOT NULL,
        [StrategyId] nvarchar(128) NOT NULL,
        [Revision] nvarchar(128) NOT NULL,
        [ArtifactSha256] nchar(64) NOT NULL,
        [ArtifactJson] nvarchar(max) NOT NULL,
        CONSTRAINT [PK_InternalTradingLedgerSnapshots] PRIMARY KEY ([Id])
    );
    CREATE UNIQUE INDEX [IX_InternalTradingLedgerSnapshots_ArtifactSha256]
        ON [dbo].[InternalTradingLedgerSnapshots] ([ArtifactSha256]);
    CREATE UNIQUE INDEX [IX_InternalTradingLedgerSnapshots_StrategyId_AsOfUtc]
        ON [dbo].[InternalTradingLedgerSnapshots] ([StrategyId], [AsOfUtc]);
END;
GO

IF NOT EXISTS (SELECT 1 FROM [dbo].[__EFMigrationsHistory]
               WHERE [MigrationId] = N'20260923060024_M38AuthoritativeReconciliation')
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260923060024_M38AuthoritativeReconciliation', N'10.0.12');
END;
GO
