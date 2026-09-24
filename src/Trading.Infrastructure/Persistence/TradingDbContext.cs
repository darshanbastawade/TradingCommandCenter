using Microsoft.EntityFrameworkCore;
using Trading.Domain.MarketData;
using Trading.Domain.Research;
using Trading.Domain.Execution;

namespace Trading.Infrastructure.Persistence;

public sealed class TradingDbContext(DbContextOptions<TradingDbContext> options) : DbContext(options)
{
    public DbSet<Instrument> Instruments => Set<Instrument>();
    public DbSet<Candle> Candles => Set<Candle>();
    public DbSet<OptionContract> OptionContracts => Set<OptionContract>();
    public DbSet<OptionQuote> OptionQuotes => Set<OptionQuote>();
    public DbSet<ResearchRun> ResearchRuns => Set<ResearchRun>();
    public DbSet<ParameterSweep> ParameterSweeps => Set<ParameterSweep>();
    public DbSet<BacktestCandidate> BacktestCandidates => Set<BacktestCandidate>();
    public DbSet<NativeCandidateVerificationRun> NativeCandidateVerificationRuns => Set<NativeCandidateVerificationRun>();
    public DbSet<IssuedStrategyCertificate> StrategyCertificates => Set<IssuedStrategyCertificate>();
    public DbSet<BacktestAnalysis> BacktestAnalyses => Set<BacktestAnalysis>();
    public DbSet<MarketFeedCapture> MarketFeedCaptures => Set<MarketFeedCapture>();
    public DbSet<PaperTradingSession> PaperTradingSessions => Set<PaperTradingSession>();
    public DbSet<LiveOrderRecord> LiveOrders => Set<LiveOrderRecord>();
    public DbSet<ControlledAutomationAuthorization> ControlledAutomationAuthorizations => Set<ControlledAutomationAuthorization>();
    public DbSet<ReconciledExecutionState> ReconciledExecutionStates => Set<ReconciledExecutionState>();
    public DbSet<InternalTradingLedgerSnapshot> InternalTradingLedgerSnapshots => Set<InternalTradingLedgerSnapshot>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var instrument = modelBuilder.Entity<Instrument>();
        instrument.ToTable("Instruments", table =>
        {
            table.HasCheckConstraint("CK_Instruments_LotSize", "[LotSize] > 0");
            table.HasCheckConstraint("CK_Instruments_TickSize", "[TickSize] > 0");
        });
        instrument.HasKey(x => x.Id);
        instrument.Property(x => x.Id).ValueGeneratedNever();
        instrument.Property(x => x.Exchange).HasMaxLength(16).IsRequired();
        instrument.Property(x => x.Symbol).HasMaxLength(64).IsRequired();
        instrument.Property(x => x.Name).HasMaxLength(128).IsRequired();
        instrument.Property(x => x.TickSize).HasColumnType("decimal(18,4)").HasPrecision(18, 4);
        instrument.HasIndex(x => new { x.Exchange, x.Symbol }).IsUnique();

        var candle = modelBuilder.Entity<Candle>();
        candle.ToTable("Candles", table =>
        {
            table.HasCheckConstraint("CK_Candles_Prices", "[Low] > 0 AND [High] >= [Low] AND [Open] >= [Low] AND [Open] <= [High] AND [Close] >= [Low] AND [Close] <= [High]");
            table.HasCheckConstraint("CK_Candles_Volume", "[Volume] >= 0");
            table.HasCheckConstraint("CK_Candles_OpenInterest", "[OpenInterest] IS NULL OR [OpenInterest] >= 0");
            table.HasCheckConstraint("CK_Candles_Timeframe", "[Timeframe] IN (1, 3, 5, 15, 30, 60, 1440)");
        });
        candle.HasKey(x => new { x.InstrumentId, x.Timeframe, x.OpenTimeUtc });
        candle.Property(x => x.OpenTimeUtc).HasColumnType("datetime2(7)")
            .HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        foreach (var property in new[] { nameof(Candle.Open), nameof(Candle.High), nameof(Candle.Low), nameof(Candle.Close) })
            candle.Property<decimal>(property).HasColumnType("decimal(18,4)").HasPrecision(18, 4);
        candle.HasOne<Instrument>().WithMany().HasForeignKey(x => x.InstrumentId).OnDelete(DeleteBehavior.Restrict);

        var optionContract = modelBuilder.Entity<OptionContract>();
        optionContract.ToTable("OptionContracts", table =>
        {
            table.HasCheckConstraint("CK_OptionContracts_Strike", "[Strike] > 0");
            table.HasCheckConstraint("CK_OptionContracts_Right", "[Right] IN (1, 2)");
            table.HasCheckConstraint("CK_OptionContracts_LotSize", "[LotSize] > 0");
            table.HasCheckConstraint("CK_OptionContracts_TickSize", "[TickSize] > 0");
        });
        optionContract.HasKey(x => x.Id);
        optionContract.Property(x => x.Id).ValueGeneratedNever();
        optionContract.Property(x => x.Exchange).HasMaxLength(16).IsRequired();
        optionContract.Property(x => x.Symbol).HasMaxLength(96).IsRequired();
        optionContract.Property(x => x.ExpiryDate).HasColumnType("date");
        optionContract.Property(x => x.Strike).HasColumnType("decimal(18,4)").HasPrecision(18, 4);
        optionContract.Property(x => x.TickSize).HasColumnType("decimal(18,4)").HasPrecision(18, 4);
        optionContract.HasIndex(x => x.Symbol).IsUnique();
        optionContract.HasIndex(x => new { x.UnderlyingInstrumentId, x.ExpiryDate, x.Right, x.Strike }).IsUnique();
        optionContract.HasOne<Instrument>().WithMany().HasForeignKey(x => x.UnderlyingInstrumentId)
            .OnDelete(DeleteBehavior.Restrict);

        var optionQuote = modelBuilder.Entity<OptionQuote>();
        optionQuote.ToTable("OptionQuotes", table =>
        {
            table.HasCheckConstraint("CK_OptionQuotes_Prices", "[Bid] > 0 AND [Ask] >= [Bid] AND [Last] > 0");
            table.HasCheckConstraint("CK_OptionQuotes_Volume", "[Volume] >= 0");
            table.HasCheckConstraint("CK_OptionQuotes_OpenInterest", "[OpenInterest] >= 0");
        });
        optionQuote.HasKey(x => new { x.OptionContractId, x.TimestampUtc });
        optionQuote.Property(x => x.TimestampUtc).HasColumnType("datetime2(7)")
            .HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        foreach (var property in new[] { nameof(OptionQuote.Bid), nameof(OptionQuote.Ask), nameof(OptionQuote.Last) })
            optionQuote.Property<decimal>(property).HasColumnType("decimal(18,4)").HasPrecision(18, 4);
        optionQuote.Ignore(x => x.Spread);
        optionQuote.Ignore(x => x.SpreadBasisPoints);
        optionQuote.HasIndex(x => x.TimestampUtc);
        optionQuote.HasOne<OptionContract>().WithMany().HasForeignKey(x => x.OptionContractId)
            .OnDelete(DeleteBehavior.Restrict);

        var research = modelBuilder.Entity<ResearchRun>();
        research.ToTable("ResearchRuns");
        research.HasKey(x => x.Id);
        research.Property(x => x.Id).ValueGeneratedNever();
        research.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(7)")
            .HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        research.Property(x => x.FromUtc).HasColumnType("datetime2(7)")
            .HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        research.Property(x => x.ToUtc).HasColumnType("datetime2(7)")
            .HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        research.Property(x => x.DataSource).HasMaxLength(128).IsRequired();
        research.Property(x => x.DataVersion).HasMaxLength(128).IsRequired();
        research.Property(x => x.CalendarId).HasMaxLength(128).IsRequired();
        research.Property(x => x.DatasetSha256).HasMaxLength(64).IsFixedLength().IsRequired();
        research.Property(x => x.ConfigurationSha256).HasMaxLength(64).IsFixedLength().IsRequired();
        research.Property(x => x.ArtifactSha256).HasMaxLength(64).IsFixedLength().IsRequired();
        research.Property(x => x.SourceRevision).HasMaxLength(128).IsRequired();
        research.Property(x => x.ArtifactJson).IsRequired();
        research.HasIndex(x => x.CreatedAtUtc);
        research.HasIndex(x => new { x.InstrumentId, x.Timeframe, x.FromUtc, x.ToUtc });
        research.HasIndex(x => new { x.DatasetSha256, x.ConfigurationSha256, x.SourceRevision }).IsUnique();
        research.HasOne<Instrument>().WithMany().HasForeignKey(x => x.InstrumentId).OnDelete(DeleteBehavior.Restrict);

        var sweep = modelBuilder.Entity<ParameterSweep>();
        sweep.ToTable("ParameterSweeps", table => table.HasCheckConstraint("CK_ParameterSweeps_Counts",
            "[EvaluatedCandidates] > 0 AND [StoredCandidates] > 0 AND [StoredCandidates] <= [EvaluatedCandidates]"));
        sweep.HasKey(x => x.Id); sweep.Property(x => x.Id).ValueGeneratedNever();
        sweep.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(7)")
            .HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        sweep.Property(x => x.StrategyId).HasMaxLength(128).IsRequired();
        sweep.Property(x => x.WorkerId).HasMaxLength(64).IsRequired();
        sweep.Property(x => x.WorkerVersion).HasMaxLength(64).IsRequired();
        foreach (var property in new[] { nameof(ParameterSweep.BaseSpecificationSha256),
                     nameof(ParameterSweep.DatasetSha256), nameof(ParameterSweep.GridSha256),
                     nameof(ParameterSweep.ArtifactSha256) })
            sweep.Property<string>(property).HasMaxLength(64).IsFixedLength().IsRequired();
        sweep.Property(x => x.ArtifactJson).IsRequired();
        sweep.HasIndex(x => x.CreatedAtUtc);
        sweep.HasIndex(x => new { x.BaseSpecificationSha256, x.GridSha256, x.WorkerId, x.WorkerVersion }).IsUnique();

        var candidate = modelBuilder.Entity<BacktestCandidate>();
        candidate.ToTable("BacktestCandidates", table =>
        {
            table.HasCheckConstraint("CK_BacktestCandidates_Rank", "[Rank] > 0");
            table.HasCheckConstraint("CK_BacktestCandidates_Status",
                "[Status] IN ('ResearchProposed', 'NativeVerified', 'NativeFailed')");
            table.HasCheckConstraint("CK_BacktestCandidates_TradeCount", "[NativeTradeCount] IS NULL OR [NativeTradeCount] >= 0");
        });
        candidate.HasKey(x => x.Id); candidate.Property(x => x.Id).ValueGeneratedNever();
        candidate.Property(x => x.StrategyId).HasMaxLength(128).IsRequired();
        foreach (var property in new[] { nameof(BacktestCandidate.SpecificationSha256),
                     nameof(BacktestCandidate.DatasetSha256), nameof(BacktestCandidate.ResearchEvidenceSha256) })
            candidate.Property<string>(property).HasMaxLength(64).IsFixedLength().IsRequired();
        candidate.Property(x => x.ResearchScore).HasColumnType("decimal(18,8)").HasPrecision(18, 8);
        candidate.Property(x => x.CandidateSpecificationJson).IsRequired();
        candidate.Property(x => x.ParametersJson).IsRequired(); candidate.Property(x => x.ResearchMetricsJson).IsRequired();
        candidate.Property(x => x.Status).HasMaxLength(32).IsRequired();
        candidate.Property(x => x.VerifiedAtUtc).HasColumnType("datetime2(7)")
            .HasConversion(value => value, value => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null);
        candidate.Property(x => x.NativeEngineId).HasMaxLength(64).IsRequired();
        candidate.Property(x => x.NativeEngineVersion).HasMaxLength(64).IsRequired();
        candidate.Property(x => x.NativeResultSha256).HasMaxLength(64).IsRequired();
        candidate.Property(x => x.NativeNetPnl).HasColumnType("decimal(18,4)").HasPrecision(18, 4);
        candidate.Property(x => x.NativeRunJson).IsRequired();
        candidate.Property(x => x.VerificationFailure).HasMaxLength(512).IsRequired();
        candidate.HasIndex(x => new { x.ParameterSweepId, x.Rank }).IsUnique();
        candidate.HasIndex(x => new { x.ParameterSweepId, x.SpecificationSha256 }).IsUnique();
        candidate.HasOne<ParameterSweep>().WithMany().HasForeignKey(x => x.ParameterSweepId).OnDelete(DeleteBehavior.Cascade);

        var verification = modelBuilder.Entity<NativeCandidateVerificationRun>();
        verification.ToTable("NativeCandidateVerificationRuns", table => table.HasCheckConstraint(
            "CK_NativeCandidateVerificationRuns_Counts",
            "[RequestedCandidates] > 0 AND [VerifiedCandidates] >= 0 AND [FailedCandidates] >= 0 AND [VerifiedCandidates] + [FailedCandidates] = [RequestedCandidates]"));
        verification.HasKey(x => x.Id); verification.Property(x => x.Id).ValueGeneratedNever();
        verification.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(7)")
            .HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        verification.Property(x => x.EngineId).HasMaxLength(64).IsRequired();
        verification.Property(x => x.EngineVersion).HasMaxLength(64).IsRequired();
        verification.Property(x => x.ArtifactSha256).HasMaxLength(64).IsFixedLength().IsRequired();
        verification.Property(x => x.ArtifactJson).IsRequired();
        verification.HasIndex(x => x.ParameterSweepId).IsUnique();
        verification.HasOne<ParameterSweep>().WithMany().HasForeignKey(x => x.ParameterSweepId)
            .OnDelete(DeleteBehavior.Restrict);

        var strategyCertificate = modelBuilder.Entity<IssuedStrategyCertificate>();
        strategyCertificate.ToTable("StrategyCertificates");
        strategyCertificate.HasKey(x => x.Id);
        strategyCertificate.Property(x => x.Id).ValueGeneratedNever();
        strategyCertificate.Property(x => x.StrategyId).HasMaxLength(128).IsRequired();
        strategyCertificate.Property(x => x.IssuedAtUtc).HasColumnType("datetime2(7)")
            .HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        strategyCertificate.Property(x => x.ExpiresAtUtc).HasColumnType("datetime2(7)")
            .HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        strategyCertificate.Property(x => x.Status).HasMaxLength(32).IsRequired();
        strategyCertificate.Property(x => x.ResearchArtifactSha256).HasMaxLength(64).IsFixedLength().IsRequired();
        strategyCertificate.Property(x => x.CertificateSha256).HasMaxLength(64).IsFixedLength().IsRequired();
        strategyCertificate.Property(x => x.CertificateJson).IsRequired();
        strategyCertificate.HasIndex(x => new { x.ResearchRunId, x.StrategyId }).IsUnique();
        strategyCertificate.HasIndex(x => x.ExpiresAtUtc);
        strategyCertificate.HasOne<ResearchRun>().WithMany().HasForeignKey(x => x.ResearchRunId)
            .OnDelete(DeleteBehavior.Restrict);

        var analysis = modelBuilder.Entity<BacktestAnalysis>();
        analysis.ToTable("BacktestAnalyses");
        analysis.HasKey(x => x.Id);
        analysis.Property(x => x.Id).ValueGeneratedNever();
        analysis.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(7)")
            .HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        analysis.Property(x => x.Deployment).HasMaxLength(128).IsRequired();
        analysis.Property(x => x.ResponseModel).HasMaxLength(128).IsRequired();
        analysis.Property(x => x.ProviderResponseId).HasMaxLength(128).IsRequired();
        analysis.Property(x => x.PromptVersion).HasMaxLength(64).IsRequired();
        analysis.Property(x => x.PromptSha256).HasMaxLength(64).IsFixedLength().IsRequired();
        analysis.Property(x => x.ResearchArtifactSha256).HasMaxLength(64).IsFixedLength().IsRequired();
        analysis.Property(x => x.AnalysisSha256).HasMaxLength(64).IsFixedLength().IsRequired();
        analysis.Property(x => x.AnalysisJson).IsRequired();
        analysis.HasIndex(x => x.CreatedAtUtc);
        analysis.HasIndex(x => new { x.ResearchRunId, x.PromptSha256, x.Deployment }).IsUnique();
        analysis.HasOne<ResearchRun>().WithMany().HasForeignKey(x => x.ResearchRunId)
            .OnDelete(DeleteBehavior.Restrict);

        var feedCapture = modelBuilder.Entity<MarketFeedCapture>();
        feedCapture.ToTable("MarketFeedCaptures", table =>
            table.HasCheckConstraint("CK_MarketFeedCaptures_TickCount", "[TickCount] > 0"));
        feedCapture.HasKey(x => x.Id);
        feedCapture.Property(x => x.Id).ValueGeneratedNever();
        feedCapture.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(7)")
            .HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        feedCapture.Property(x => x.FirstReceivedAtUtc).HasColumnType("datetime2(7)")
            .HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        feedCapture.Property(x => x.LastReceivedAtUtc).HasColumnType("datetime2(7)")
            .HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        feedCapture.Property(x => x.Source).HasMaxLength(32).IsRequired();
        feedCapture.Property(x => x.QuoteMode).HasMaxLength(16).IsRequired();
        feedCapture.Property(x => x.ArtifactSha256).HasMaxLength(64).IsFixedLength().IsRequired();
        feedCapture.Property(x => x.ArtifactJson).IsRequired();
        feedCapture.HasIndex(x => x.CreatedAtUtc);
        feedCapture.HasIndex(x => new { x.Source, x.CreatedAtUtc });

        var paperSession = modelBuilder.Entity<PaperTradingSession>();
        paperSession.ToTable("PaperTradingSessions", table =>
        {
            table.HasCheckConstraint("CK_PaperTradingSessions_Cash", "[InitialCash] > 0 AND [EndingCash] >= 0");
            table.HasCheckConstraint("CK_PaperTradingSessions_Counts",
                "[SubmittedOrders] > 0 AND [FilledTrades] >= 0 AND [RejectedOrders] >= 0 AND [FilledTrades] + [RejectedOrders] = [SubmittedOrders]");
            table.HasCheckConstraint("CK_PaperTradingSessions_QualificationLineage",
                "([StrategyQualificationId] IS NULL AND [StrategyQualificationSha256] IS NULL AND " +
                "[QualificationCertificateId] IS NULL AND [QualificationCertificateSha256] IS NULL AND " +
                "[QualificationStartedAtUtc] IS NULL) OR ([StrategyQualificationId] IS NOT NULL AND " +
                "[StrategyQualificationSha256] IS NOT NULL AND [QualificationCertificateId] IS NOT NULL AND " +
                "[QualificationCertificateSha256] IS NOT NULL AND [QualificationStartedAtUtc] IS NOT NULL AND " +
                "[CreatedAtUtc] >= [QualificationStartedAtUtc])");
        });
        paperSession.HasKey(x => x.Id);
        paperSession.Property(x => x.Id).ValueGeneratedNever();
        paperSession.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(7)")
            .HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        paperSession.Property(x => x.StrategyId).HasMaxLength(128).IsRequired();
        foreach (var property in new[] { nameof(PaperTradingSession.InitialCash),
                     nameof(PaperTradingSession.EndingCash), nameof(PaperTradingSession.RealizedNetPnl) })
            paperSession.Property<decimal>(property).HasColumnType("decimal(18,4)").HasPrecision(18, 4);
        paperSession.Property(x => x.ArtifactSha256).HasMaxLength(64).IsFixedLength().IsRequired();
        paperSession.Property(x => x.ConfigurationSha256).HasMaxLength(64).IsFixedLength().IsRequired();
        paperSession.Property(x => x.ArtifactJson).IsRequired();
        paperSession.Property(x => x.StrategyQualificationSha256).HasMaxLength(64).IsFixedLength();
        paperSession.Property(x => x.QualificationCertificateSha256).HasMaxLength(64).IsFixedLength();
        paperSession.Property(x => x.QualificationStartedAtUtc).HasColumnType("datetime2(7)")
            .HasConversion(value => value, value => value.HasValue ?
                DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null);
        paperSession.HasIndex(x => x.CreatedAtUtc);
        paperSession.HasIndex(x => new { x.StrategyQualificationId, x.CreatedAtUtc });
        paperSession.HasIndex(x => new { x.StrategyCertificateId, x.MarketFeedCaptureId,
            x.ConfigurationSha256 }).IsUnique();
        paperSession.HasOne<IssuedStrategyCertificate>().WithMany().HasForeignKey(x => x.StrategyCertificateId)
            .OnDelete(DeleteBehavior.Restrict);
        paperSession.HasOne<MarketFeedCapture>().WithMany().HasForeignKey(x => x.MarketFeedCaptureId)
            .OnDelete(DeleteBehavior.Restrict);

        var liveOrder = modelBuilder.Entity<LiveOrderRecord>();
        liveOrder.ToTable("LiveOrders", table =>
        {
            table.HasCheckConstraint("CK_LiveOrders_Mode", "[Mode] IN ('SemiLive', 'DirectLive')");
            table.HasCheckConstraint("CK_LiveOrders_Status", "[Status] IN ('Proposed', 'Prepared', 'Submitted')");
            table.HasCheckConstraint("CK_LiveOrders_Prices",
                "[Quantity] > 0 AND [StopPrice] > 0 AND [StopPrice] < [LimitPrice] AND [TargetPrice] > [LimitPrice]");
        });
        liveOrder.HasKey(x => x.Id);
        liveOrder.Property(x => x.Id).ValueGeneratedNever();
        liveOrder.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(7)")
            .HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        liveOrder.Property(x => x.Mode).HasMaxLength(16).IsRequired();
        liveOrder.Property(x => x.Status).HasMaxLength(16).IsRequired();
        liveOrder.Property(x => x.StrategyId).HasMaxLength(128).IsRequired();
        liveOrder.Property(x => x.Exchange).HasMaxLength(16).IsRequired();
        liveOrder.Property(x => x.TradingSymbol).HasMaxLength(96).IsRequired();
        foreach (var property in new[] { nameof(LiveOrderRecord.LimitPrice), nameof(LiveOrderRecord.StopPrice),
                     nameof(LiveOrderRecord.TargetPrice) })
            liveOrder.Property<decimal>(property).HasColumnType("decimal(18,4)").HasPrecision(18, 4);
        liveOrder.Property(x => x.PaperEvidenceSha256).HasMaxLength(64).IsFixedLength().IsRequired();
        liveOrder.Property(x => x.RiskDecisionSha256).HasMaxLength(64).IsFixedLength().IsRequired();
        liveOrder.Property(x => x.BrokerOrderId).HasMaxLength(64).IsRequired();
        liveOrder.Property(x => x.ArtifactSha256).HasMaxLength(64).IsFixedLength().IsRequired();
        liveOrder.Property(x => x.ArtifactJson).IsRequired();
        liveOrder.HasIndex(x => x.CreatedAtUtc);
        liveOrder.HasIndex(x => x.RequestId).IsUnique();
        liveOrder.HasOne<IssuedStrategyCertificate>().WithMany().HasForeignKey(x => x.StrategyCertificateId)
            .OnDelete(DeleteBehavior.Restrict);

        var authorization = modelBuilder.Entity<ControlledAutomationAuthorization>();
        authorization.ToTable("ControlledAutomationAuthorizations", table =>
        {
            table.HasCheckConstraint("CK_ControlledAutomationAuthorizations_Decision",
                "[Decision] = 'DirectSubmissionEligible'");
            table.HasCheckConstraint("CK_ControlledAutomationAuthorizations_Actions",
                "[MaximumAuthorizedActions] = 1 AND [ConsumedActions] = 1");
            table.HasCheckConstraint("CK_ControlledAutomationAuthorizations_Validity",
                "[EvaluatedAtUtc] <= [FirstConsumedAtUtc] AND [FirstConsumedAtUtc] < [ExpiresAtUtc]");
        });
        authorization.HasKey(x => x.AutomationDecisionId);
        authorization.Property(x => x.AutomationDecisionId).ValueGeneratedNever();
        authorization.Property(x => x.AutomationSha256).HasMaxLength(64).IsFixedLength().IsRequired();
        authorization.Property(x => x.ActionReference).HasMaxLength(128).IsRequired();
        authorization.Property(x => x.StrategyId).HasMaxLength(128).IsRequired();
        authorization.Property(x => x.Decision).HasMaxLength(32).IsRequired();
        foreach (var property in new[] { nameof(ControlledAutomationAuthorization.EvaluatedAtUtc),
                     nameof(ControlledAutomationAuthorization.ExpiresAtUtc),
                     nameof(ControlledAutomationAuthorization.FirstConsumedAtUtc),
                     nameof(ControlledAutomationAuthorization.LastConsumedAtUtc) })
            authorization.Property<DateTime>(property).HasColumnType("datetime2(7)")
                .HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        authorization.HasIndex(x => x.AutomationSha256).IsUnique();
        authorization.HasIndex(x => x.ActionId).IsUnique();
        authorization.HasIndex(x => x.RelatedLiveOrderId).IsUnique();
        authorization.HasOne<LiveOrderRecord>().WithOne().HasForeignKey<ControlledAutomationAuthorization>(x => x.RelatedLiveOrderId)
            .OnDelete(DeleteBehavior.Restrict);

        var executionState = modelBuilder.Entity<ReconciledExecutionState>();
        executionState.ToTable("ReconciledExecutionStates", table =>
        {
            table.HasCheckConstraint("CK_ReconciledExecutionStates_Counts",
                "[UnresolvedBrokerSubmissions] >= 0 AND [ActiveOpenBrokerPositions] >= 0");
        });
        executionState.HasKey(x => x.Id);
        executionState.Property(x => x.Id).ValueGeneratedNever();
        executionState.Property(x => x.AsOfUtc).HasColumnType("datetime2(7)")
            .HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        executionState.Property(x => x.ExchangeTradingDate).HasColumnType("date");
        executionState.Property(x => x.RealizedPnlToday).HasColumnType("decimal(18,4)").HasPrecision(18, 4);
        executionState.Property(x => x.ReconciliationSha256).HasMaxLength(64).IsFixedLength().IsRequired();
        executionState.Property(x => x.SourceRevision).HasMaxLength(128).IsRequired();
        executionState.HasIndex(x => new { x.ExchangeTradingDate, x.AsOfUtc }).IsUnique();
        executionState.HasIndex(x => x.ReconciliationId).IsUnique();

        var internalLedger = modelBuilder.Entity<InternalTradingLedgerSnapshot>();
        internalLedger.ToTable("InternalTradingLedgerSnapshots");
        internalLedger.HasKey(x => x.Id);
        internalLedger.Property(x => x.Id).ValueGeneratedNever();
        internalLedger.Property(x => x.AsOfUtc).HasColumnType("datetime2(7)")
            .HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        internalLedger.Property(x => x.StrategyId).HasMaxLength(128).IsRequired();
        internalLedger.Property(x => x.Revision).HasMaxLength(128).IsRequired();
        internalLedger.Property(x => x.ArtifactSha256).HasMaxLength(64).IsFixedLength().IsRequired();
        internalLedger.Property(x => x.ArtifactJson).IsRequired();
        internalLedger.HasIndex(x => new { x.StrategyId, x.AsOfUtc }).IsUnique();
        internalLedger.HasIndex(x => x.ArtifactSha256).IsUnique();
    }
}
