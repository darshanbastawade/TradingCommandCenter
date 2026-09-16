SET XACT_ABORT ON;
GO

IF NOT EXISTS (SELECT 1 FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20260915160000_M17RealOptionsBacktesting')
BEGIN
    BEGIN TRANSACTION;
    CREATE TABLE [OptionContracts] (
        [Id] uniqueidentifier NOT NULL,
        [UnderlyingInstrumentId] uniqueidentifier NOT NULL,
        [Exchange] nvarchar(16) NOT NULL,
        [Symbol] nvarchar(96) NOT NULL,
        [ExpiryDate] date NOT NULL,
        [Strike] decimal(18,4) NOT NULL,
        [Right] int NOT NULL,
        [LotSize] int NOT NULL,
        [TickSize] decimal(18,4) NOT NULL,
        CONSTRAINT [PK_OptionContracts] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_OptionContracts_LotSize] CHECK ([LotSize] > 0),
        CONSTRAINT [CK_OptionContracts_Right] CHECK ([Right] IN (1, 2)),
        CONSTRAINT [CK_OptionContracts_Strike] CHECK ([Strike] > 0),
        CONSTRAINT [CK_OptionContracts_TickSize] CHECK ([TickSize] > 0),
        CONSTRAINT [FK_OptionContracts_Instruments_UnderlyingInstrumentId]
            FOREIGN KEY ([UnderlyingInstrumentId]) REFERENCES [Instruments] ([Id]) ON DELETE NO ACTION
    );
    CREATE TABLE [OptionQuotes] (
        [OptionContractId] uniqueidentifier NOT NULL,
        [TimestampUtc] datetime2(7) NOT NULL,
        [Bid] decimal(18,4) NOT NULL,
        [Ask] decimal(18,4) NOT NULL,
        [Last] decimal(18,4) NOT NULL,
        [Volume] bigint NOT NULL,
        [OpenInterest] bigint NOT NULL,
        CONSTRAINT [PK_OptionQuotes] PRIMARY KEY ([OptionContractId], [TimestampUtc]),
        CONSTRAINT [CK_OptionQuotes_OpenInterest] CHECK ([OpenInterest] >= 0),
        CONSTRAINT [CK_OptionQuotes_Prices] CHECK ([Bid] > 0 AND [Ask] >= [Bid] AND [Last] > 0),
        CONSTRAINT [CK_OptionQuotes_Volume] CHECK ([Volume] >= 0),
        CONSTRAINT [FK_OptionQuotes_OptionContracts_OptionContractId]
            FOREIGN KEY ([OptionContractId]) REFERENCES [OptionContracts] ([Id]) ON DELETE NO ACTION
    );
    CREATE UNIQUE INDEX [IX_OptionContracts_Symbol] ON [OptionContracts] ([Symbol]);
    CREATE UNIQUE INDEX [IX_OptionContracts_UnderlyingInstrumentId_ExpiryDate_Right_Strike]
        ON [OptionContracts] ([UnderlyingInstrumentId], [ExpiryDate], [Right], [Strike]);
    CREATE INDEX [IX_OptionQuotes_TimestampUtc] ON [OptionQuotes] ([TimestampUtc]);
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
        VALUES (N'20260915160000_M17RealOptionsBacktesting', N'10.0.12');
    COMMIT;
END;
GO
