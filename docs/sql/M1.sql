IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260914071129_InitialMarketData'
)
BEGIN
    CREATE TABLE [Instruments] (
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
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260914071129_InitialMarketData'
)
BEGIN
    CREATE TABLE [Candles] (
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
        CONSTRAINT [FK_Candles_Instruments_InstrumentId] FOREIGN KEY ([InstrumentId]) REFERENCES [Instruments] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260914071129_InitialMarketData'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Instruments_Exchange_Symbol] ON [Instruments] ([Exchange], [Symbol]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260914071129_InitialMarketData'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260914071129_InitialMarketData', N'10.0.12');
END;

COMMIT;
GO

