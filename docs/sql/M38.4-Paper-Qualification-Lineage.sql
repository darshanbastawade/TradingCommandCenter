USE [Market];
GO

IF COL_LENGTH(N'dbo.PaperTradingSessions', N'QualificationCertificateId') IS NULL
    ALTER TABLE [dbo].[PaperTradingSessions] ADD [QualificationCertificateId] uniqueidentifier NULL;
IF COL_LENGTH(N'dbo.PaperTradingSessions', N'QualificationCertificateSha256') IS NULL
    ALTER TABLE [dbo].[PaperTradingSessions] ADD [QualificationCertificateSha256] nchar(64) NULL;
IF COL_LENGTH(N'dbo.PaperTradingSessions', N'QualificationStartedAtUtc') IS NULL
    ALTER TABLE [dbo].[PaperTradingSessions] ADD [QualificationStartedAtUtc] datetime2(7) NULL;
IF COL_LENGTH(N'dbo.PaperTradingSessions', N'StrategyQualificationId') IS NULL
    ALTER TABLE [dbo].[PaperTradingSessions] ADD [StrategyQualificationId] uniqueidentifier NULL;
IF COL_LENGTH(N'dbo.PaperTradingSessions', N'StrategyQualificationSha256') IS NULL
    ALTER TABLE [dbo].[PaperTradingSessions] ADD [StrategyQualificationSha256] nchar(64) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PaperTradingSessions_StrategyQualificationId_CreatedAtUtc'
               AND object_id = OBJECT_ID(N'[dbo].[PaperTradingSessions]'))
    CREATE INDEX [IX_PaperTradingSessions_StrategyQualificationId_CreatedAtUtc]
        ON [dbo].[PaperTradingSessions] ([StrategyQualificationId], [CreatedAtUtc]);
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
               WHERE name = N'CK_PaperTradingSessions_QualificationLineage')
    ALTER TABLE [dbo].[PaperTradingSessions] WITH CHECK ADD
        CONSTRAINT [CK_PaperTradingSessions_QualificationLineage] CHECK
        (([StrategyQualificationId] IS NULL AND [StrategyQualificationSha256] IS NULL AND
          [QualificationCertificateId] IS NULL AND [QualificationCertificateSha256] IS NULL AND
          [QualificationStartedAtUtc] IS NULL) OR
         ([StrategyQualificationId] IS NOT NULL AND [StrategyQualificationSha256] IS NOT NULL AND
          [QualificationCertificateId] IS NOT NULL AND [QualificationCertificateSha256] IS NOT NULL AND
          [QualificationStartedAtUtc] IS NOT NULL AND [CreatedAtUtc] >= [QualificationStartedAtUtc]));
GO

IF NOT EXISTS (SELECT 1 FROM [dbo].[__EFMigrationsHistory]
               WHERE [MigrationId] = N'20260923052717_M38PaperQualificationLineage')
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260923052717_M38PaperQualificationLineage', N'10.0.12');
END;
GO
