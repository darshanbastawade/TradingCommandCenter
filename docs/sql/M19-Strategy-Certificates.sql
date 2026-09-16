USE [Market];
GO

IF OBJECT_ID(N'[dbo].[StrategyCertificates]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[StrategyCertificates]
    (
        [Id] uniqueidentifier NOT NULL,
        [ResearchRunId] uniqueidentifier NOT NULL,
        [StrategyId] nvarchar(128) NOT NULL,
        [IssuedAtUtc] datetime2(7) NOT NULL,
        [ExpiresAtUtc] datetime2(7) NOT NULL,
        [Status] nvarchar(32) NOT NULL,
        [ResearchArtifactSha256] nchar(64) NOT NULL,
        [CertificateSha256] nchar(64) NOT NULL,
        [CertificateJson] nvarchar(max) NOT NULL,
        CONSTRAINT [PK_StrategyCertificates] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_StrategyCertificates_ResearchRuns_ResearchRunId]
            FOREIGN KEY ([ResearchRunId]) REFERENCES [dbo].[ResearchRuns] ([Id]) ON DELETE NO ACTION
    );
    CREATE INDEX [IX_StrategyCertificates_ExpiresAtUtc]
        ON [dbo].[StrategyCertificates] ([ExpiresAtUtc]);
    CREATE UNIQUE INDEX [IX_StrategyCertificates_ResearchRunId_StrategyId]
        ON [dbo].[StrategyCertificates] ([ResearchRunId], [StrategyId]);
END;
GO

IF NOT EXISTS (SELECT 1 FROM [dbo].[__EFMigrationsHistory]
               WHERE [MigrationId] = N'20260915200000_M19StrategyCertificates')
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260915200000_M19StrategyCertificates', N'10.0.12');
END;
GO
