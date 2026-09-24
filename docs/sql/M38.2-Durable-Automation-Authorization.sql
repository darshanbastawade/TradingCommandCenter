USE [Market];
GO

IF OBJECT_ID(N'[dbo].[ControlledAutomationAuthorizations]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[ControlledAutomationAuthorizations]
    (
        [AutomationDecisionId] uniqueidentifier NOT NULL,
        [AutomationSha256] nchar(64) NOT NULL,
        [ActionId] uniqueidentifier NOT NULL,
        [ActionReference] nvarchar(128) NOT NULL,
        [StrategyId] nvarchar(128) NOT NULL,
        [EvaluatedAtUtc] datetime2(7) NOT NULL,
        [ExpiresAtUtc] datetime2(7) NOT NULL,
        [Decision] nvarchar(32) NOT NULL,
        [MaximumAuthorizedActions] int NOT NULL,
        [ConsumedActions] int NOT NULL,
        [FirstConsumedAtUtc] datetime2(7) NOT NULL,
        [LastConsumedAtUtc] datetime2(7) NOT NULL,
        [RelatedLiveOrderId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_ControlledAutomationAuthorizations] PRIMARY KEY ([AutomationDecisionId]),
        CONSTRAINT [CK_ControlledAutomationAuthorizations_Decision]
            CHECK ([Decision] = 'DirectSubmissionEligible'),
        CONSTRAINT [CK_ControlledAutomationAuthorizations_Actions]
            CHECK ([MaximumAuthorizedActions] = 1 AND [ConsumedActions] = 1),
        CONSTRAINT [CK_ControlledAutomationAuthorizations_Validity]
            CHECK ([EvaluatedAtUtc] <= [FirstConsumedAtUtc] AND [FirstConsumedAtUtc] < [ExpiresAtUtc]),
        CONSTRAINT [FK_ControlledAutomationAuthorizations_LiveOrders_RelatedLiveOrderId]
            FOREIGN KEY ([RelatedLiveOrderId]) REFERENCES [dbo].[LiveOrders] ([Id]) ON DELETE NO ACTION
    );
    CREATE UNIQUE INDEX [IX_ControlledAutomationAuthorizations_ActionId]
        ON [dbo].[ControlledAutomationAuthorizations] ([ActionId]);
    CREATE UNIQUE INDEX [IX_ControlledAutomationAuthorizations_AutomationSha256]
        ON [dbo].[ControlledAutomationAuthorizations] ([AutomationSha256]);
    CREATE UNIQUE INDEX [IX_ControlledAutomationAuthorizations_RelatedLiveOrderId]
        ON [dbo].[ControlledAutomationAuthorizations] ([RelatedLiveOrderId]);
END;
GO

IF NOT EXISTS (SELECT 1 FROM [dbo].[__EFMigrationsHistory]
               WHERE [MigrationId] = N'20260923042928_M38DurableAutomationAuthorization')
BEGIN
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260923042928_M38DurableAutomationAuthorization', N'10.0.12');
END;
GO
