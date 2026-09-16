USE [Market];
GO

IF OBJECT_ID(N'[dbo].[MarketFeedCaptures]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[MarketFeedCaptures]
    (
        [Id] uniqueidentifier NOT NULL,
        [CreatedAtUtc] datetime2(7) NOT NULL,
        [Source] nvarchar(32) NOT NULL,
        [QuoteMode] nvarchar(16) NOT NULL,
        [TickCount] int NOT NULL,
        [FirstReceivedAtUtc] datetime2(7) NOT NULL,
        [LastReceivedAtUtc] datetime2(7) NOT NULL,
        [ArtifactSha256] nchar(64) NOT NULL,
        [ArtifactJson] nvarchar(max) NOT NULL,
        CONSTRAINT [PK_MarketFeedCaptures] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_MarketFeedCaptures_TickCount] CHECK ([TickCount] > 0)
    );
    CREATE INDEX [IX_MarketFeedCaptures_CreatedAtUtc] ON [dbo].[MarketFeedCaptures] ([CreatedAtUtc]);
    CREATE INDEX [IX_MarketFeedCaptures_Source_CreatedAtUtc]
        ON [dbo].[MarketFeedCaptures] ([Source], [CreatedAtUtc]);
END;
GO

IF NOT EXISTS (SELECT 1 FROM [dbo].[__EFMigrationsHistory]
               WHERE [MigrationId] = N'20260915220000_M21ZerodhaMarketFeeds')
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260915220000_M21ZerodhaMarketFeeds', N'10.0.12');
END;
GO
