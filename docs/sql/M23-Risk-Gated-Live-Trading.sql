USE [Market];
GO

IF OBJECT_ID(N'[dbo].[LiveOrders]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[LiveOrders]
    (
        [Id] uniqueidentifier NOT NULL,
        [StrategyCertificateId] uniqueidentifier NOT NULL,
        [RequestId] uniqueidentifier NOT NULL,
        [CreatedAtUtc] datetime2(7) NOT NULL,
        [Mode] nvarchar(16) NOT NULL,
        [Status] nvarchar(16) NOT NULL,
        [StrategyId] nvarchar(128) NOT NULL,
        [Exchange] nvarchar(16) NOT NULL,
        [TradingSymbol] nvarchar(96) NOT NULL,
        [Quantity] int NOT NULL,
        [LimitPrice] decimal(18,4) NOT NULL,
        [StopPrice] decimal(18,4) NOT NULL,
        [TargetPrice] decimal(18,4) NOT NULL,
        [PaperEvidenceSha256] nchar(64) NOT NULL,
        [RiskDecisionSha256] nchar(64) NOT NULL,
        [BrokerOrderId] nvarchar(64) NOT NULL,
        [ArtifactSha256] nchar(64) NOT NULL,
        [ArtifactJson] nvarchar(max) NOT NULL,
        CONSTRAINT [PK_LiveOrders] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_LiveOrders_Mode] CHECK ([Mode] IN ('SemiLive', 'DirectLive')),
        CONSTRAINT [CK_LiveOrders_Status] CHECK ([Status] IN ('Proposed', 'Prepared', 'Submitted')),
        CONSTRAINT [CK_LiveOrders_Prices]
            CHECK ([Quantity] > 0 AND [StopPrice] > 0 AND [StopPrice] < [LimitPrice] AND [TargetPrice] > [LimitPrice]),
        CONSTRAINT [FK_LiveOrders_StrategyCertificates_StrategyCertificateId]
            FOREIGN KEY ([StrategyCertificateId]) REFERENCES [dbo].[StrategyCertificates] ([Id]) ON DELETE NO ACTION
    );
    CREATE INDEX [IX_LiveOrders_CreatedAtUtc] ON [dbo].[LiveOrders] ([CreatedAtUtc]);
    CREATE UNIQUE INDEX [IX_LiveOrders_RequestId] ON [dbo].[LiveOrders] ([RequestId]);
    CREATE INDEX [IX_LiveOrders_StrategyCertificateId] ON [dbo].[LiveOrders] ([StrategyCertificateId]);
END;
GO

IF NOT EXISTS (SELECT 1 FROM [dbo].[__EFMigrationsHistory]
               WHERE [MigrationId] = N'20260916000000_M23RiskGatedLiveTrading')
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260916000000_M23RiskGatedLiveTrading', N'10.0.12');
END;
GO
