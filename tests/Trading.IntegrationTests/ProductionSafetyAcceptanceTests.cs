using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Trading.Api;
using Trading.Application.AI;
using Trading.Application.Backtesting;
using Trading.Application.Execution;
using Trading.Application.Research;
using Trading.Backtesting.Certification;
using Trading.Backtesting.Comparison;
using Trading.Backtesting.Ranking;
using Trading.Backtesting.Reporting;
using Trading.Backtesting.Robustness;
using Trading.Domain.Execution;
using Trading.Domain.MarketData;
using Trading.Domain.Research;
using Trading.Execution.Automation;
using Trading.Execution.Live;
using Trading.Execution.Paper;
using Trading.Execution.Qualification;
using Trading.Execution.Reconciliation;
using Trading.MarketData.Quality;

namespace Trading.IntegrationTests;

/// <summary>
/// M38.11 acceptance boundary. External engines, AI and Zerodha are represented only by sealed fixture
/// evidence or fakes; every safety decision and hash verification uses production code.
/// </summary>
public sealed class ProductionSafetyAcceptanceTests
{
    private const string Strategy = "vwap-ema-trend-breakout-v1";
    private const string ActionReference = "m38.11-direct-action";
    private static readonly JsonSerializerOptions Json = Options();

    [Fact]
    public async Task Complete_research_to_direct_live_chain_submits_exactly_once()
    {
        using var fixture = await AcceptanceFixture.CreateAsync();

        var result = await fixture.ExecuteAsync();

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Equal(1, fixture.Broker.PlaceCalls);
        Assert.Equal(1, fixture.Authorizations.ConsumeCalls);
        Assert.True(fixture.CertificateV2.EligibleForQualificationPipeline);
        Assert.True(fixture.QualifiedStrategy.EligibleForPaperQualification);
        Assert.True(fixture.PaperQualification.Status == PaperQualificationStatus.Qualified,
            string.Join(',', fixture.PaperQualification.FailureCodes));
        Assert.Equal(LiveReconciliationStatus.Reconciled, fixture.Reconciliation.Status);
        Assert.Equal(ControlledAutomationDecision.DirectSubmissionEligible, fixture.Authorization.Decision);
        Assert.Single(fixture.Orders.Records);
    }

    [Theory]
    [InlineData("dataset-hash")]
    [InlineData("specification-hash")]
    [InlineData("m30-verdict")]
    [InlineData("robustness-qualification")]
    [InlineData("m32-status")]
    [InlineData("expired-certificate")]
    [InlineData("m34-qualification")]
    [InlineData("m35-qualification")]
    [InlineData("reconciliation")]
    [InlineData("m37-decision")]
    [InlineData("m37-expiry")]
    [InlineData("m37-replay")]
    [InlineData("m18-risk")]
    [InlineData("kill-switch")]
    [InlineData("direct-order-opt-in")]
    [InlineData("operator-confirmation")]
    [InlineData("stale-quote")]
    [InlineData("stale-account")]
    [InlineData("unexpected-position")]
    [InlineData("prior-unresolved-submission")]
    public async Task Breaking_any_evidence_or_execution_gate_prevents_broker_submission(string fault)
    {
        using var fixture = await AcceptanceFixture.CreateAsync(fault);

        var result = await fixture.ExecuteAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(0, fixture.Broker.PlaceCalls);
    }

    [Fact]
    public void Direct_broker_call_has_one_source_path_after_M37_and_M18_gates()
    {
        var root = RepositoryRoot();
        var sourceRoot = Path.Combine(root, "src");
        var callSites = Directory.GetFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .SelectMany(path => File.ReadLines(path).Select((line, index) => new { path, line, index }))
            .Where(item => item.line.Contains(".PlaceLimitBuyAsync(", StringComparison.Ordinal))
            .ToArray();

        var only = Assert.Single(callSites);
        Assert.EndsWith(Path.Combine("Trading.Api", "LiveTradingCommands.cs"), only.path,
            StringComparison.OrdinalIgnoreCase);
        var source = File.ReadAllText(only.path);
        var call = source.IndexOf(".PlaceLimitBuyAsync(", StringComparison.Ordinal);
        Assert.True(source.IndexOf("ReadAndVerifyAuthorizationAsync", StringComparison.Ordinal) < call);
        Assert.True(source.IndexOf("LiveTradingEngine.Prepare", StringComparison.Ordinal) < call);
        Assert.True(source.IndexOf("TryConsumeAsync", StringComparison.Ordinal) < call);
    }

    private sealed class AcceptanceFixture : IDisposable
    {
        private readonly string fault;
        private readonly string directory;
        private readonly string inputPath;
        private readonly string authorizationPath;
        private readonly string outputPath;
        private readonly string calendarPath;
        private readonly IssuedStrategyCertificate legacyCertificate;
        private readonly IReadOnlyList<PaperTradingSession> sessions;
        private readonly LiveEntryIntent intent;

        private AcceptanceFixture(string fault, string directory, string inputPath,
            string authorizationPath, string outputPath, string calendarPath,
            StrategyCertificateV2 certificateV2, QualifiedStrategyArtifact qualifiedStrategy,
            PaperQualificationArtifact paperQualification, LiveReconciliationArtifact reconciliation,
            ControlledAutomationArtifact authorization, IssuedStrategyCertificate legacyCertificate,
            IReadOnlyList<PaperTradingSession> sessions, LiveEntryIntent intent, FakeBroker broker,
            AuthorizationStore authorizations, OrderStore orders)
        {
            this.fault = fault; this.directory = directory; this.inputPath = inputPath;
            this.authorizationPath = authorizationPath; this.outputPath = outputPath;
            this.calendarPath = calendarPath; CertificateV2 = certificateV2;
            QualifiedStrategy = qualifiedStrategy; PaperQualification = paperQualification;
            Reconciliation = reconciliation; Authorization = authorization;
            this.legacyCertificate = legacyCertificate; this.sessions = sessions; this.intent = intent;
            Broker = broker; Authorizations = authorizations; Orders = orders;
        }

        public StrategyCertificateV2 CertificateV2 { get; }
        public QualifiedStrategyArtifact QualifiedStrategy { get; }
        public PaperQualificationArtifact PaperQualification { get; }
        public LiveReconciliationArtifact Reconciliation { get; }
        public ControlledAutomationArtifact Authorization { get; }
        public FakeBroker Broker { get; }
        public AuthorizationStore Authorizations { get; }
        public OrderStore Orders { get; }

        public static async Task<AcceptanceFixture> CreateAsync(string fault = "")
        {
            var directory = Path.Combine(Path.GetTempPath(), $"m3811-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var now = DateTime.UtcNow;
            var researchCreated = now.AddDays(-11);
            var researchRunId = Guid.NewGuid();
            var datasetHash = Hash('a');
            var specification = Specification(datasetHash);
            var score = new StrategyScore(1, Strategy, 95, true, [], 18, 16, 14, 18, 14, 10, 5);
            var ranking = new StrategyRankingResult(1, [score], [score]);
            var dataset = new DatasetQualityCertificate(3, true, "acceptance-fixture", "v1",
                "m38.11-calendar", specification.Specification.Instrument.InstrumentId,
                Timeframe.Minute5, new(2026, 1, 5), new(2026, 1, 6), 1, 1, 1, 75, 75, 75, 75, 0,
                Hash('9'), fault == "dataset-hash" ? Hash('8') : datasetHash, [], []);
            var research = new ResearchIntegrityArtifact(1, researchRunId, researchCreated, "m38.11-source",
                dataset, Hash('7'), new(1, 1, 0, "rolling", 100_000, 750, 100_000, 2, 5,
                    "india-options-v1", new()),
                [new StrategyResearchEvidence(Strategy, null!, null!, null!, [], null!, null!)], ranking);

            var native = Run("native-csharp", BacktestEngineRole.Authoritative, specification, datasetHash);
            if (fault == "specification-hash")
                native = BacktestRunCodec.Seal(native with { SpecificationSha256 = Hash('6'), ResultSha256 = string.Empty });
            // Model the M27 retained candidate and the M28 authoritative native verification record.
            var sweepId = Guid.NewGuid();
            var sweep = new ParameterSweep(sweepId, new DateTimeOffset(researchCreated), Strategy,
                "vectorbt", "1.1.0", specification.SpecificationSha256, datasetHash, Hash('5'),
                1, 1, Hash('4'), "{\"schemaVersion\":1}");
            var candidate = new BacktestCandidate(Guid.NewGuid(), sweepId, 1, Strategy,
                specification.SpecificationSha256, datasetHash,
                BacktestSpecificationCodec.Serialize(specification), "{\"candidate\":1}", 100,
                "{\"score\":100}", Hash('3'));
            candidate.Apply(new(candidate.Id, true, researchCreated.AddMinutes(30), native.EngineId,
                native.EngineVersion, native.ResultSha256, native.NetPnl, native.Trades.Count,
                BacktestRunCodec.Serialize(native), string.Empty));
            sweep.MarkNativeVerificationCompleted();
            _ = new NativeCandidateVerificationRun(Guid.NewGuid(), sweepId,
                new DateTimeOffset(researchCreated.AddMinutes(30)), native.EngineId, native.EngineVersion,
                1, 1, 0, Hash('2'), "{\"schemaVersion\":1}");
            if (candidate.Status != BacktestCandidateStatus.NativeVerified ||
                !sweep.NativeVerificationCompleted)
                throw new InvalidOperationException("M27/M28 fixture did not reach native verification.");
            var lean = Run("lean", BacktestEngineRole.IndependentValidation, specification, datasetHash, true,
                fault == "m30-verdict");
            var comparison = CrossEngineTradeComparer.Compare(
                fault == "specification-hash" ? Run("native-csharp", BacktestEngineRole.Authoritative,
                    specification, datasetHash) : native, lean);
            var robustness = RobustnessSuiteAnalyzer.Analyze(
                fault == "specification-hash" ? Run("native-csharp", BacktestEngineRole.Authoritative,
                    specification, datasetHash) : native,
                new RobustnessSuiteSettings { Iterations = 100 }, researchCreated.AddHours(1));
            if (fault == "robustness-qualification")
            {
                var stressed = robustness.CostStress.Select((item, index) => index == 2
                    ? item with { NetPnl = -1, FinalCapital = robustness.InitialCapital - 1 }
                    : item).ToArray();
                robustness = RobustnessSuiteArtifactCodec.Seal(robustness with
                { CostStress = stressed, ArtifactSha256 = string.Empty });
            }

            var researchPath = Path.Combine(directory, "research.json");
            var specificationPath = Path.Combine(directory, "specification.json");
            var nativePath = Path.Combine(directory, "native.json");
            var comparisonPath = Path.Combine(directory, "comparison.json");
            var robustnessPath = Path.Combine(directory, "robustness.json");
            var certificatePath = Path.Combine(directory, "certificate-v2.json");
            await File.WriteAllTextAsync(researchPath, JsonSerializer.Serialize(research, Json));
            await File.WriteAllTextAsync(specificationPath, BacktestSpecificationCodec.Serialize(specification));
            await File.WriteAllTextAsync(nativePath, BacktestRunCodec.Serialize(native));
            await File.WriteAllTextAsync(comparisonPath, CrossEngineComparisonCodec.Serialize(comparison));
            await File.WriteAllTextAsync(robustnessPath, RobustnessSuiteArtifactCodec.Serialize(robustness));
            var error = new StringWriter();
            var certificateExit = await StrategyCertificateV2Commands.RunAsync(
                ["issue-certificate-v2", "--research", researchPath, "--specification", specificationPath,
                 "--native", nativePath, "--comparison", comparisonPath, "--robustness", robustnessPath,
                 "--output", certificatePath], new ConfigurationBuilder().Build(), TextWriter.Null, error);

            if (fault is "dataset-hash" or "specification-hash")
                return Early(directory, fault, certificateExit, error.ToString());
            var certificate = JsonSerializer.Deserialize<StrategyCertificateV2>(
                await File.ReadAllTextAsync(certificatePath), Json)!;
            if (fault is "m30-verdict" or "robustness-qualification")
                return Early(directory, fault, certificateExit, string.Join(',', certificate.EvidenceFailures), certificate);
            if (certificateExit != 0 || !StrategyCertificateV2Issuer.Verify(certificate))
                throw new InvalidOperationException($"Acceptance M32 failed: {error}");

            if (fault == "m32-status")
                certificate = StrategyCertificateV2Issuer.Seal(certificate with
                { Status = StrategyCertificateV2Status.EvidenceRejected,
                  EvidenceFailures = ["acceptance-forced-rejection"],
                  EligibleForQualificationPipeline = false, CertificateSha256 = string.Empty });
            var analysis = Analysis(certificate, researchCreated.AddHours(2));
            QualifiedStrategyArtifact qualified;
            try
            {
                var qualificationTime = fault == "expired-certificate" ? certificate.ExpiresAtUtc : now.AddDays(-10);
                qualified = QualifiedStrategyPipeline.Qualify(certificate, analysis,
                    fault == "m34-qualification" ? "x" : "m38.11-human-review", qualificationTime);
            }
            catch (Exception exception) when (fault is "m32-status" or "expired-certificate" or "m34-qualification")
            { return Early(directory, fault, 2, exception.Message, certificate); }

            var legacy = LegacyCertificate(researchRunId, researchCreated, score, ranking, datasetHash);
            var sessions = Enumerable.Range(0, 10).Select(index => PaperSession(legacy, qualified,
                qualified.QualifiedAtUtc.AddDays(index).AddHours(1),
                fault == "m35-qualification" ? -100 : 100)).ToArray();
            var verified = sessions.Select(item => new VerifiedPaperSession(item.Id, item.CreatedAtUtc,
                DateOnly.FromDateTime(item.CreatedAtUtc), item.StrategyId, item.ArtifactSha256,
                item.SubmittedOrders, item.FilledTrades, item.RejectedOrders, item.RealizedNetPnl)).ToArray();
            var cutoff = sessions.Max(item => item.CreatedAtUtc);
            var paper = PaperQualificationEngine.EvaluateDurable(qualified.QualificationId,
                qualified.QualificationSha256, qualified.CertificateId, qualified.CertificateSha256,
                Strategy, qualified.QualifiedAtUtc, cutoff, certificate.ExpiresAtUtc,
                verified.Length, verified, []);
            if (fault == "m35-qualification")
                return Early(directory, fault, 4, string.Join(',', paper.FailureCodes), certificate,
                    qualified, paper, legacy, sessions);

            var snapshot = new LiveReconciliationSnapshot(now.AddSeconds(-2), 30_000,
                fault == "reconciliation" ? 29_000 : 30_000, 1, [], [], [], []);
            var provenance = new LiveReconciliationProvenance(
                LiveReconciliationSourceMode.AuthoritativeBroker, "zerodha-kite", "fixture-account",
                Hash('4'), Hash('5'), "m38.11-revision");
            var reconciliation = LiveReconciliationEngine.Reconcile(paper.PaperQualificationId,
                paper.PaperQualificationSha256, Strategy, snapshot, now.AddSeconds(-1), provenance: provenance);
            if (fault == "reconciliation")
                return Early(directory, fault, 4, string.Join(',', reconciliation.DiscrepancyCodes), certificate,
                    qualified, paper, legacy, sessions, reconciliation);

            var requestId = Guid.NewGuid();
            var automationMode = fault == "m37-decision" ? ControlledAutomationMode.Observe :
                ControlledAutomationMode.DirectLive;
            var state = new ControlledAutomationStateSnapshot(now.AddSeconds(-1), IndiaDate(now), true,
                0, 0, 0, fault == "prior-unresolved-submission" ? 1 : 0, 0, Hash('3'), string.Empty);
            var automationIntent = new ControlledAutomationIntent(requestId, automationMode,
                ActionReference, 1, true, "ALLOW-CONTROLLED-AUTOMATION");
            var qualifiedPath = Path.Combine(directory, "qualified-strategy.json");
            var paperPath = Path.Combine(directory, "paper-qualification.json");
            var reconciliationPath = Path.Combine(directory, "reconciliation.json");
            var automationIntentPath = Path.Combine(directory, "automation-intent.json");
            var authorizationPath = Path.Combine(directory, "authorization.json");
            await File.WriteAllTextAsync(qualifiedPath, QualifiedStrategyPipeline.Serialize(qualified));
            await File.WriteAllTextAsync(paperPath, PaperQualificationEngine.Serialize(paper));
            await File.WriteAllTextAsync(reconciliationPath, LiveReconciliationEngine.Serialize(reconciliation));
            await File.WriteAllTextAsync(automationIntentPath, JsonSerializer.Serialize(automationIntent, Json));
            await using (var automationServices = new ServiceCollection()
                .AddSingleton<IControlledAutomationStateProvider>(new StateProvider(state))
                .BuildServiceProvider())
            {
                var automationConfiguration = new ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ControlledAutomation:Enabled"] = "true",
                        ["ControlledAutomation:AllowSemiLive"] = "true",
                        ["ControlledAutomation:AllowDirectLive"] = "true",
                        ["ControlledAutomation:KillSwitchEngaged"] = "false"
                    }).Build();
                var automationError = new StringWriter();
                var automationExit = await ControlledAutomationCommands.RunAsync(
                    ["evaluate-automation", "--qualified-strategy", qualifiedPath,
                     "--paper-qualification", paperPath, "--reconciliation", reconciliationPath,
                     "--file", automationIntentPath, "--output", authorizationPath],
                    automationServices, automationConfiguration, TextWriter.Null, automationError);
                var expectedExit = fault == "prior-unresolved-submission" ? 4 : 0;
                if (automationExit != expectedExit)
                    throw new InvalidOperationException($"M37 command failed: {automationError}");
            }
            var authorization = JsonSerializer.Deserialize<ControlledAutomationArtifact>(
                await File.ReadAllTextAsync(authorizationPath), Json)!;
            if (fault == "m37-expiry")
            {
                authorization = ControlledAutomationEngine.Seal(authorization with
                { EvaluatedAtUtc = now.AddMinutes(-2), ExpiresAtUtc = now.AddMinutes(-1), AutomationSha256 = string.Empty });
                await File.WriteAllTextAsync(authorizationPath,
                    ControlledAutomationEngine.Serialize(authorization));
            }

            var broker = new FakeBroker(fault);
            var authorizations = new AuthorizationStore(fault == "m37-replay" ? authorization.AutomationDecisionId : null);
            var orders = new OrderStore();
            var intent = new LiveEntryIntent(requestId, specification.Specification.Instrument.InstrumentId,
                12345, "NFO", "TESTCE", now, now.AddSeconds(1), 100.2m,
                fault == "m18-risk" ? 50m : 90m, 110, 25, 2,
                fault != "operator-confirmation") { AutomationActionReference = ActionReference };
            var inputPath = Path.Combine(directory, "live-input.json");
            var outputPath = Path.Combine(directory, "live-order.json");
            var calendarPath = Path.Combine(directory, "calendar.csv");
            await File.WriteAllTextAsync(inputPath, JsonSerializer.Serialize(intent, Json));
            await File.WriteAllTextAsync(calendarPath,
                $"special,{now:yyyy-MM-dd},00:00,23:59{Environment.NewLine}");
            return new(fault, directory, inputPath, authorizationPath, outputPath, calendarPath,
                certificate, qualified, paper, reconciliation, authorization, legacy, sessions, intent,
                broker, authorizations, orders);
        }

        public async Task<AcceptanceResult> ExecuteAsync()
        {
            if (Broker is NullBroker)
                return new(2, fault);
            await using var services = new ServiceCollection()
                .AddSingleton<IStrategyCertificateStore>(new CertificateStore(legacyCertificate))
                .AddSingleton<IPaperTradingSessionStore>(new PaperStore(sessions))
                .AddSingleton<ILiveOrderStore>(Orders)
                .AddSingleton<IControlledAutomationAuthorizationStore>(Authorizations)
                .AddSingleton<ILiveBrokerClient>(Broker).BuildServiceProvider();
            var confirm = fault == "operator-confirmation" ? "WRONG" : "PLACE-LIVE-ORDER";
            var args = new[] { "live-order", "--mode", "direct", "--certificate-id",
                legacyCertificate.Id.ToString(), "--file", inputPath, "--output", outputPath,
                "--confirm", confirm, "--automation-authorization", authorizationPath };
            var error = new StringWriter();
            var exit = await LiveTradingCommands.RunAsync(args, services, Configuration(), TextWriter.Null, error);
            return new(exit, error.ToString());
        }

        private IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["LiveTrading:KillSwitchEngaged"] = fault == "kill-switch" ? "true" : "false",
                ["LiveTrading:AllowDirectOrders"] = fault == "direct-order-opt-in" ? "false" : "true",
                ["LiveTrading:MinimumPaperSessions"] = "10",
                ["LiveTrading:MinimumPaperFilledTrades"] = "10",
                ["LiveTrading:MaximumBrokerDataAgeSeconds"] = "30",
                ["Zerodha:AllowLiveOrders"] = "true",
                ["ExchangeCalendar:Id"] = "m38.11-calendar",
                ["ExchangeCalendar:TimeZoneId"] = "UTC",
                ["ExchangeCalendar:DefaultSessionOpen"] = "00:00:00",
                ["ExchangeCalendar:DefaultSessionClose"] = "23:59:59",
                ["ExchangeCalendar:FilePath"] = calendarPath,
                ["RiskPolicy:EntryWindowStart"] = "00:00:00",
                ["RiskPolicy:LastEntryTime"] = "23:59:58",
                ["RiskPolicy:MandatoryExitTime"] = "23:59:59"
            }).Build();

        public void Dispose()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        private static AcceptanceFixture Early(string directory, string fault, int exit, string error,
            StrategyCertificateV2? certificate = null, QualifiedStrategyArtifact? qualified = null,
            PaperQualificationArtifact? paper = null, IssuedStrategyCertificate? legacy = null,
            IReadOnlyList<PaperTradingSession>? sessions = null, LiveReconciliationArtifact? reconciliation = null)
        {
            if (exit == 0) throw new InvalidOperationException($"Fault '{fault}' unexpectedly passed: {error}");
            var broker = new NullBroker();
            return new(fault, directory, string.Empty, string.Empty, string.Empty, string.Empty,
                certificate ?? PlaceholderCertificate(), qualified ?? PlaceholderQualification(),
                paper ?? PlaceholderPaper(), reconciliation ?? PlaceholderReconciliation(),
                PlaceholderAuthorization(), legacy ?? PlaceholderLegacy(), sessions ?? [],
                PlaceholderIntent(), broker, new(), new());
        }
    }

    private static SealedBacktestSpecification Specification(string datasetHash) =>
        BacktestSpecificationCodec.Seal(new(1, Strategy,
            new(Guid.Parse("38110000-0000-0000-0000-000000000001"), "NFO", "TESTCE",
                BacktestAssetClass.IndexOption, "INR", 25, .05m),
            new(new(2026, 1, 5, 3, 45, 0, DateTimeKind.Utc),
                new(2026, 1, 6, 10, 0, 0, DateTimeKind.Utc), 5, "Asia/Kolkata",
                "m38.11-calendar", "certified", "v1", datasetHash, BacktestMarketDataMode.OhlcvBars),
            new(100_000, 750, 30_000, 2),
            new(3, 5, "india-options-v1", new(15, 25), BacktestSignalTiming.CompletedBar,
                BacktestEntryFillPolicy.NextObservedBarOpen, BacktestAmbiguousBarPolicy.StopFirst,
                BacktestEndOfDataPolicy.CloseLastObserved),
            new Dictionary<string, decimal> { ["fastEmaPeriod"] = 3, ["slowEmaPeriod"] = 6,
                ["atrPeriod"] = 3, ["adxPeriod"] = 3, ["volumeAveragePeriod"] = 3,
                ["breakoutLookbackBars"] = 3, ["minimumAdx"] = 5,
                ["volumeMultiplier"] = 1.1m, ["atrStopMultiple"] = 1,
                ["rewardRiskMultiple"] = 3, ["entryWindowStartMinuteOfDay"] = 555,
                ["entryWindowEndMinuteOfDay"] = 930 }));

    private static BacktestRun Run(string engine, BacktestEngineRole role,
        SealedBacktestSpecification specification, string datasetHash, bool external = false,
        bool divergent = false)
    {
        var start = new DateTime(2026, 1, 5, 4, 0, 0, DateTimeKind.Utc);
        var capital = 100_000m;
        var trades = Enumerable.Range(0, 4).Select(index =>
        {
            var net = divergent && index == 3 ? 250m : 500m;
            capital += net;
            return new BacktestRunTrade(Strategy, specification.Specification.Instrument.InstrumentId,
                BacktestRunTradeDirection.Long, start.AddMinutes(index * 30),
                start.AddMinutes(index * 30 + 5), start.AddMinutes(index * 30 + 10),
                25, 100, 90, 120, divergent && index == 3 ? 110 : 120, "target",
                net, 0, net, capital);
        }).ToArray();
        var run = new BacktestRun(1, engine, "m38.11", role, specification.SpecificationSha256,
            datasetHash, Hash('c'), 100_000, capital, trades.Sum(item => item.NetPnl),
            trades.Count(item => item.NetPnl > 0), 0, trades, [], string.Empty)
        {
            ExternalValidation = external ? new("QuantConnect LEAN",
                $"quantconnect/lean@sha256:{Hash('d')}", Hash('e'), Hash('f'),
                "lean-vwap-v1", Guid.NewGuid().ToString("N"), Hash('1'), Hash('2')) : null
        };
        return BacktestRunCodec.Seal(run);
    }

    private static AstraResearchAnalysisV2Artifact Analysis(StrategyCertificateV2 certificate, DateTime created)
    {
        var output = new AstraResearchAnalystV2Output(2, certificate.CertificateId, Strategy,
            "Sealed fixture analysis", ["research"], ["comparison"], ["robustness"],
            ["human review required"], ["paper qualification"], ["not trading authority"],
            ResearchAnalystV2Recommendation.QualificationReview);
        return AstraResearchAnalysisV2Codec.Seal(new(2, Guid.NewGuid(), created,
            certificate.CertificateId, Strategy, certificate.CertificateSha256, Hash('a'),
            "fixture-no-network", "fixture-model", "fixture-response", "m38.11-analysis-v1",
            Hash('b'), 0, 0, output, false, string.Empty));
    }

    private static IssuedStrategyCertificate LegacyCertificate(Guid researchRunId, DateTime created,
        StrategyScore score, StrategyRankingResult ranking, string datasetHash)
    {
        var value = Assert.Single(StrategyCertificateIssuer.Issue(new(researchRunId, created,
            Guid.Parse("38110000-0000-0000-0000-000000000001"), 5,
            new(2026, 1, 5, 3, 45, 0, DateTimeKind.Utc),
            new(2026, 1, 6, 10, 0, 0, DateTimeKind.Utc), datasetHash, Hash('7'), Hash('6'),
            "m38.11-source", new(750, 100_000, 2, 5, "india-options-v1"), [Strategy], ranking)));
        return new(value.CertificateId, researchRunId, Strategy, value.IssuedAtUtc, value.ExpiresAtUtc,
            value.Status.ToString(), value.ResearchArtifactSha256, value.CertificateSha256,
            JsonSerializer.Serialize(value, Json));
    }

    private static PaperTradingSession PaperSession(IssuedStrategyCertificate legacy,
        QualifiedStrategyArtifact qualification, DateTime created, decimal pnl)
    {
        var id = Guid.NewGuid(); var capture = Guid.NewGuid();
        var result = new PaperTradingResult(2, id, created, Strategy, 30_000, 30_000 + pnl,
            pnl, 0, pnl, 4, 4, 0, []);
        var unsigned = new QualifiedPaperTradingSessionArtifact(2, id, created, legacy.Id,
            legacy.CertificateSha256, capture, Hash('4'), "m18-v2", "india-options-v1", Hash('5'),
            true, result, qualification.QualificationId, qualification.QualificationSha256,
            qualification.CertificateId, qualification.CertificateSha256,
            qualification.QualifiedAtUtc, string.Empty);
        var hash = Sha256(JsonSerializer.Serialize(unsigned, Json));
        var json = JsonSerializer.Serialize(unsigned with { ArtifactSha256 = hash }, Json);
        return new(id, legacy.Id, capture, created, Strategy, result.InitialCash, result.EndingCash,
            result.RealizedNetPnl, 4, 4, 0, unsigned.ConfigurationSha256, hash, json,
            qualification.QualificationId, qualification.QualificationSha256,
            qualification.CertificateId, qualification.CertificateSha256, qualification.QualifiedAtUtc);
    }

    private static DateOnly IndiaDate(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc,
        TimeZoneInfo.FindSystemTimeZoneById("India Standard Time")));
    private static string Hash(char value) => new(value, 64);
    private static string Sha256(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static StrategyCertificateV2 PlaceholderCertificate() => StrategyCertificateV2Issuer.Issue(new(
        Guid.NewGuid(), DateTime.UtcNow.AddDays(-1), Strategy, "placeholder", Hash('a'), Hash('b'), Hash('c'),
        Hash('d'), Hash('e'), Hash('f'), Hash('1'), Hash('2'),
        new(1, Strategy, 90, true, [], 15, 15, 15, 15, 15, 10, 5), true, true, 1, 1, 1,
        0, 0, 0, 1, 0, 0, 100, 10, 10, 10, 10));
    private static QualifiedStrategyArtifact PlaceholderQualification()
    {
        var certificate = PlaceholderCertificate();
        return QualifiedStrategyPipeline.Qualify(certificate, Analysis(certificate, certificate.IssuedAtUtc),
            "placeholder-review", certificate.IssuedAtUtc);
    }
    private static PaperQualificationArtifact PlaceholderPaper()
    {
        var qualified = PlaceholderQualification();
        return PaperQualificationEngine.EvaluateDurable(qualified.QualificationId,
            qualified.QualificationSha256, qualified.CertificateId, qualified.CertificateSha256,
            Strategy, qualified.QualifiedAtUtc, qualified.QualifiedAtUtc.AddMinutes(1),
            qualified.ExpiresAtUtc, 0, [], []);
    }
    private static LiveReconciliationArtifact PlaceholderReconciliation()
    {
        var paper = PlaceholderPaper(); var now = DateTime.UtcNow;
        return LiveReconciliationEngine.Reconcile(paper.PaperQualificationId,
            paper.PaperQualificationSha256, Strategy, new(now, 1, 1, 0, [], [], [], []), now);
    }
    private static ControlledAutomationArtifact PlaceholderAuthorization()
    {
        var now = DateTime.UtcNow; return ControlledAutomationEngine.Evaluate(Guid.NewGuid(), Hash('a'),
            Guid.NewGuid(), Hash('b'), Guid.NewGuid(), Hash('c'), Strategy, now,
            new(Guid.NewGuid(), ControlledAutomationMode.Observe, "placeholder", 1, false, null),
            new(now, IndiaDate(now), false, 0, 0, 0, 0, 0, string.Empty, "placeholder"), now);
    }
    private static IssuedStrategyCertificate PlaceholderLegacy() => new(Guid.NewGuid(), Guid.NewGuid(),
        Strategy, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), "ResearchQualified",
        Hash('a'), Hash('b'), "{}");
    private static LiveEntryIntent PlaceholderIntent() => new(Guid.NewGuid(), Guid.NewGuid(), 1,
        "NFO", "NONE", DateTime.UtcNow, DateTime.UtcNow.AddSeconds(1), 100, 90, 110, 25, 1, false);

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null;
             directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "TradingCommandCenter.sln")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false));
        return options;
    }

    private sealed record AcceptanceResult(int ExitCode, string Error);
    private class FakeBroker(string fault) : ILiveBrokerClient
    {
        public int PlaceCalls { get; private set; }
        public Task<BrokerAccountSnapshot> GetAccountSnapshotAsync(CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;
            var asOf = fault == "stale-account" ? now.AddMinutes(-1) : now;
            IReadOnlyList<BrokerPosition> positions = fault == "unexpected-position"
                ? [new(12345, "NFO", "TESTCE", "MIS", 25)] : [];
            return Task.FromResult(new BrokerAccountSnapshot(asOf, 30_000, positions, 0));
        }
        public Task<BrokerQuote> GetQuoteAsync(uint instrumentToken, string exchange, string tradingSymbol,
            CancellationToken cancellationToken = default)
        {
            var now = fault == "stale-quote" ? DateTime.UtcNow.AddMinutes(-1) : DateTime.UtcNow;
            return Task.FromResult(new BrokerQuote(now, instrumentToken, exchange, tradingSymbol,
                100, 99.9m, 100));
        }
        public Task<BrokerOrderReceipt> PlaceLimitBuyAsync(BrokerOrderRequest request,
            CancellationToken cancellationToken = default)
        { PlaceCalls++; return Task.FromResult(new BrokerOrderReceipt("fixture-order", DateTime.UtcNow)); }
    }
    private sealed class NullBroker() : FakeBroker("early");
    private sealed class AuthorizationStore(Guid? alreadyConsumed = null) : IControlledAutomationAuthorizationStore
    {
        private readonly HashSet<Guid> consumed = alreadyConsumed is { } id ? [id] : [];
        public int ConsumeCalls { get; private set; }
        public Task<bool> TryConsumeAsync(ControlledAutomationReservation value, DateTime consumedAtUtc,
            CancellationToken cancellationToken = default)
        { ConsumeCalls++; return Task.FromResult(consumed.Add(value.AutomationDecisionId)); }
        public Task<bool> IsConsumedAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(consumed.Contains(id));
    }
    private sealed class StateProvider(ControlledAutomationStateSnapshot value) :
        IControlledAutomationStateProvider
    {
        public Task<ControlledAutomationStateSnapshot> GetAsync(DateTime evaluatedAtUtc,
            CancellationToken cancellationToken = default) => Task.FromResult(value);
    }
    private sealed class CertificateStore(IssuedStrategyCertificate value) : IStrategyCertificateStore
    {
        public Task<IssuedStrategyCertificate?> FindAsync(Guid id, CancellationToken token = default) =>
            Task.FromResult(id == value.Id ? value : null);
        public Task AddRangeAsync(IReadOnlyList<IssuedStrategyCertificate> values,
            CancellationToken token = default) => Task.CompletedTask;
        public Task<IReadOnlyList<StrategyCertificateSummary>> ListAsync(int limit = 100,
            CancellationToken token = default) => Task.FromResult<IReadOnlyList<StrategyCertificateSummary>>([]);
    }
    private sealed class PaperStore(IReadOnlyList<PaperTradingSession> values) : IPaperTradingSessionStore
    {
        public Task AddAsync(PaperTradingSession value, CancellationToken token = default) => Task.CompletedTask;
        public Task<PaperTradingSession?> FindAsync(Guid id, CancellationToken token = default) =>
            Task.FromResult(values.SingleOrDefault(item => item.Id == id));
        public Task<IReadOnlyList<PaperTradingSessionSummary>> ListAsync(int limit = 100,
            CancellationToken token = default) => Task.FromResult<IReadOnlyList<PaperTradingSessionSummary>>([]);
        public Task<IReadOnlyList<PaperTradingSession>> ListForCertificateAsync(Guid id,
            CancellationToken token = default) => Task.FromResult<IReadOnlyList<PaperTradingSession>>(
                values.Where(item => item.StrategyCertificateId == id).ToArray());
    }
    private sealed class OrderStore : ILiveOrderStore
    {
        public List<LiveOrderRecord> Records { get; } = [];
        public Task AddAsync(LiveOrderRecord value, CancellationToken token = default)
        { Records.Add(value); return Task.CompletedTask; }
        public Task UpdateAsync(LiveOrderRecord value, CancellationToken token = default) => Task.CompletedTask;
        public Task<LiveOrderRecord?> FindAsync(Guid id, CancellationToken token = default) =>
            Task.FromResult(Records.SingleOrDefault(item => item.Id == id));
        public Task<IReadOnlyList<LiveOrderSummary>> ListAsync(int limit = 100,
            CancellationToken token = default) => Task.FromResult<IReadOnlyList<LiveOrderSummary>>([]);
    }
}
