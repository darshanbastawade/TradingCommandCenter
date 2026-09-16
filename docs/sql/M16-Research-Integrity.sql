SET XACT_ABORT ON;
GO

IF NOT EXISTS (
    SELECT 1 FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260915124000_M16ResearchIntegrity'
)
BEGIN
    BEGIN TRANSACTION;

    CREATE TABLE [ResearchRuns] (
        [Id] uniqueidentifier NOT NULL,
        [CreatedAtUtc] datetime2(7) NOT NULL,
        [InstrumentId] uniqueidentifier NOT NULL,
        [Timeframe] int NOT NULL,
        [FromUtc] datetime2(7) NOT NULL,
        [ToUtc] datetime2(7) NOT NULL,
        [DataSource] nvarchar(128) NOT NULL,
        [DataVersion] nvarchar(128) NOT NULL,
        [CalendarId] nvarchar(128) NOT NULL,
        [DatasetSha256] nchar(64) NOT NULL,
        [ConfigurationSha256] nchar(64) NOT NULL,
        [ArtifactSha256] nchar(64) NOT NULL,
        [SourceRevision] nvarchar(128) NOT NULL,
        [ArtifactJson] nvarchar(max) NOT NULL,
        CONSTRAINT [PK_ResearchRuns] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ResearchRuns_Instruments_InstrumentId]
            FOREIGN KEY ([InstrumentId]) REFERENCES [Instruments] ([Id]) ON DELETE NO ACTION
    );

    CREATE INDEX [IX_ResearchRuns_CreatedAtUtc]
        ON [ResearchRuns] ([CreatedAtUtc]);

    CREATE UNIQUE INDEX [IX_ResearchRuns_DatasetSha256_ConfigurationSha256_SourceRevision]
        ON [ResearchRuns] ([DatasetSha256], [ConfigurationSha256], [SourceRevision]);

    CREATE INDEX [IX_ResearchRuns_InstrumentId_Timeframe_FromUtc_ToUtc]
        ON [ResearchRuns] ([InstrumentId], [Timeframe], [FromUtc], [ToUtc]);

    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260915124000_M16ResearchIntegrity', N'10.0.12');

    COMMIT;
END;
GO
