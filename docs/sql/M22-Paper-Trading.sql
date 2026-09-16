USE [Market];
GO

IF OBJECT_ID(N'[dbo].[PaperTradingSessions]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[PaperTradingSessions]
    (
        [Id] uniqueidentifier NOT NULL,
        [StrategyCertificateId] uniqueidentifier NOT NULL,
        [MarketFeedCaptureId] uniqueidentifier NOT NULL,
        [CreatedAtUtc] datetime2(7) NOT NULL,
        [StrategyId] nvarchar(128) NOT NULL,
        [InitialCash] decimal(18,4) NOT NULL,
        [EndingCash] decimal(18,4) NOT NULL,
        [RealizedNetPnl] decimal(18,4) NOT NULL,
        [SubmittedOrders] int NOT NULL,
        [FilledTrades] int NOT NULL,
        [RejectedOrders] int NOT NULL,
        [ConfigurationSha256] nchar(64) NOT NULL,
        [ArtifactSha256] nchar(64) NOT NULL,
        [ArtifactJson] nvarchar(max) NOT NULL,
        CONSTRAINT [PK_PaperTradingSessions] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_PaperTradingSessions_Cash]
            CHECK ([InitialCash] > 0 AND [EndingCash] >= 0),
        CONSTRAINT [CK_PaperTradingSessions_Counts]
            CHECK ([SubmittedOrders] > 0 AND [FilledTrades] >= 0 AND [RejectedOrders] >= 0
                AND [FilledTrades] + [RejectedOrders] = [SubmittedOrders]),
        CONSTRAINT [FK_PaperTradingSessions_StrategyCertificates_StrategyCertificateId]
            FOREIGN KEY ([StrategyCertificateId]) REFERENCES [dbo].[StrategyCertificates] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_PaperTradingSessions_MarketFeedCaptures_MarketFeedCaptureId]
            FOREIGN KEY ([MarketFeedCaptureId]) REFERENCES [dbo].[MarketFeedCaptures] ([Id]) ON DELETE NO ACTION
    );
    CREATE INDEX [IX_PaperTradingSessions_CreatedAtUtc]
        ON [dbo].[PaperTradingSessions] ([CreatedAtUtc]);
    CREATE INDEX [IX_PaperTradingSessions_MarketFeedCaptureId]
        ON [dbo].[PaperTradingSessions] ([MarketFeedCaptureId]);
    CREATE UNIQUE INDEX [IX_PaperTradingSessions_StrategyCertificateId_MarketFeedCaptureId_ConfigurationSha256]
        ON [dbo].[PaperTradingSessions] ([StrategyCertificateId], [MarketFeedCaptureId], [ConfigurationSha256]);
END;
GO

IF NOT EXISTS (SELECT 1 FROM [dbo].[__EFMigrationsHistory]
               WHERE [MigrationId] = N'20260915230000_M22PaperTrading')
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260915230000_M22PaperTrading', N'10.0.12');
END;
GO
