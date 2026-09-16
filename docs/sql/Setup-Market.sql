-- Run in SSMS connected to DESKTOP-EF1NCS7 using Windows Authentication.
-- Creates Market only if absent; does not drop, truncate or overwrite existing tables.
USE [master];
GO
IF DB_ID(N'Market') IS NULL
    EXEC(N'CREATE DATABASE [Market]');
GO
USE [Market];
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO
-- Stop before changing a database that has a conflicting pre-existing schema.
IF DB_NAME() <> N'Market'
    THROW 51002, 'Market database could not be selected. No schema changes will be applied.', 1;
IF (OBJECT_ID(N'dbo.Instruments', N'U') IS NOT NULL OR OBJECT_ID(N'dbo.Candles', N'U') IS NOT NULL)
BEGIN
    IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL
        THROW 51000, 'Existing trading tables have no EF migration history. Review the schema before applying this script.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20260914071129_InitialMarketData')
        THROW 51001, 'Existing trading tables are not associated with the expected migration. Review the schema first.', 1;
END;
IF OBJECT_ID(N'[dbo].[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [dbo].[__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260914071129_InitialMarketData'
)
BEGIN
    CREATE TABLE [dbo].[Instruments] (
        [Id] uniqueidentifier NOT NULL,
        [Exchange] nvarchar(16) NOT NULL,
        [Symbol] nvarchar(64) NOT NULL,
        [Name] nvarchar(128) NOT NULL,
        [LotSize] int NOT NULL,
        [TickSize] decimal(18,4) NOT NULL,
        CONSTRAINT [PK_Instruments] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Instruments_LotSize] CHECK ([LotSize] > 0),
        CONSTRAINT [CK_Instruments_TickSize] CHECK ([TickSize] > 0)
    );
END;

IF NOT EXISTS (
    SELECT * FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260914071129_InitialMarketData'
)
BEGIN
    CREATE TABLE [dbo].[Candles] (
        [InstrumentId] uniqueidentifier NOT NULL,
        [Timeframe] int NOT NULL,
        [OpenTimeUtc] datetime2(7) NOT NULL,
        [Open] decimal(18,4) NOT NULL,
        [High] decimal(18,4) NOT NULL,
        [Low] decimal(18,4) NOT NULL,
        [Close] decimal(18,4) NOT NULL,
        [Volume] bigint NOT NULL,
        [OpenInterest] bigint NULL,
        CONSTRAINT [PK_Candles] PRIMARY KEY ([InstrumentId], [Timeframe], [OpenTimeUtc]),
        CONSTRAINT [CK_Candles_OpenInterest] CHECK ([OpenInterest] IS NULL OR [OpenInterest] >= 0),
        CONSTRAINT [CK_Candles_Prices] CHECK ([Low] > 0 AND [High] >= [Low] AND [Open] >= [Low] AND [Open] <= [High] AND [Close] >= [Low] AND [Close] <= [High]),
        CONSTRAINT [CK_Candles_Timeframe] CHECK ([Timeframe] IN (1, 3, 5, 15, 30, 60, 1440)),
        CONSTRAINT [CK_Candles_Volume] CHECK ([Volume] >= 0),
        CONSTRAINT [FK_Candles_Instruments_InstrumentId] FOREIGN KEY ([InstrumentId]) REFERENCES [dbo].[Instruments] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260914071129_InitialMarketData'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Instruments_Exchange_Symbol] ON [dbo].[Instruments] ([Exchange], [Symbol]);
END;

IF NOT EXISTS (
    SELECT * FROM [dbo].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260914071129_InitialMarketData'
)
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260914071129_InitialMarketData', N'10.0.12');
END;

COMMIT;

SELECT DB_NAME() AS DatabaseName, s.name AS SchemaName, t.name AS TableName
FROM sys.tables AS t JOIN sys.schemas AS s ON s.schema_id=t.schema_id
WHERE s.name=N'dbo' AND t.name IN (N'Instruments',N'Candles',N'__EFMigrationsHistory')
ORDER BY t.name;
SELECT MigrationId, ProductVersion FROM dbo.__EFMigrationsHistory;
GO
