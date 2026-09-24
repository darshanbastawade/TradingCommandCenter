using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trading.Backtesting.Certification;
using Trading.Application.Execution;
using Trading.Execution.Automation;
using Trading.Execution.Qualification;
using Trading.Execution.Reconciliation;

namespace Trading.Api;

public static class PaperQualificationCommands
{
    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] == "qualify-paper";

    public static async Task<int> RunAsync(string[] args, IServiceProvider services,
        IConfiguration configuration, TextWriter output, TextWriter error, CancellationToken token = default)
    {
        string? created = null;
        try
        {
            var command = ProgressionArtifactIO.Parse(args, "--qualified-strategy", "--file", "--output");
            var qualified = await ProgressionArtifactIO.ReadAsync<QualifiedStrategyArtifact>(command["--qualified-strategy"], token);
            var nowUtc = DateTime.UtcNow;
            if (!QualifiedStrategyPipeline.Verify(qualified) || !qualified.EligibleForPaperQualification ||
                qualified.SemiLiveAuthorized || qualified.DirectLiveAuthorized || nowUtc >= qualified.ExpiresAtUtc)
                throw new InvalidDataException("The M34 qualified strategy is invalid, expired, or not eligible for paper qualification.");
            var input = await ProgressionArtifactIO.ReadAsync<PaperQualificationFileInput>(command["--file"], token);
            if (input.QualificationCutoffUtc.Kind != DateTimeKind.Utc ||
                input.QualificationCutoffUtc < qualified.QualifiedAtUtc ||
                input.QualificationCutoffUtc > nowUtc || input.QualificationCutoffUtc >= qualified.ExpiresAtUtc)
                throw new ArgumentException("The qualification cutoff must be UTC, within the M34 window, and not in the future.");
            await using var scope = services.CreateAsyncScope();
            var discovered = await scope.ServiceProvider.GetRequiredService<IPaperQualificationSessionQuery>()
                .ListForQualificationAsync(qualified.QualificationId, qualified.QualifiedAtUtc,
                    input.QualificationCutoffUtc, token);
            var sessions = new List<VerifiedPaperSession>();
            var rejected = new List<RejectedPaperSession>();
            var ids = new HashSet<Guid>(); var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var stored in discovered)
            {
                var reason = ValidateStoredSession(stored, qualified, ids, hashes, out var verified);
                if (reason is null) sessions.Add(verified!);
                else rejected.Add(new(stored.Id, reason));
            }
            var policy = configuration.GetSection("PaperQualification").Get<PaperQualificationPolicy>() ?? new();
            var artifact = PaperQualificationEngine.EvaluateDurable(qualified.QualificationId,
                qualified.QualificationSha256, qualified.CertificateId, qualified.CertificateSha256,
                qualified.StrategyId, qualified.QualifiedAtUtc, input.QualificationCutoffUtc,
                qualified.ExpiresAtUtc, discovered.Count, sessions, rejected, policy);
            await ProgressionArtifactIO.WriteNewAsync(command["--output"],
                PaperQualificationEngine.Serialize(artifact), token); created = command["--output"];
            await output.WriteLineAsync(ProgressionArtifactIO.Serialize(new { status = artifact.Status,
                artifact.PaperQualificationId, artifact.StrategyId, artifact.SessionCount, artifact.FilledTrades,
                artifact.AggregateNetPnl, artifact.FailureCodes, output = command["--output"],
                artifact.PaperQualificationSha256 }));
            created = null; return artifact.Status == PaperQualificationStatus.Qualified ? 0 : 4;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { ProgressionArtifactIO.Delete(created); await error.WriteLineAsync("Paper qualification cancelled."); return 130; }
        catch (Exception exception) when (ProgressionArtifactIO.Expected(exception))
        { ProgressionArtifactIO.Delete(created); await error.WriteLineAsync(exception.Message); return 2; }
    }

    private static string? ValidateStoredSession(Trading.Domain.Execution.PaperTradingSession stored,
        QualifiedStrategyArtifact qualified, HashSet<Guid> ids, HashSet<string> hashes,
        out VerifiedPaperSession? verified)
    {
        verified = null;
        if (!ids.Add(stored.Id)) return "duplicate-session-id";
        if (!hashes.Add(stored.ArtifactSha256)) return "duplicate-session-hash";
        if (stored.StrategyQualificationId != qualified.QualificationId ||
            stored.StrategyQualificationSha256 != qualified.QualificationSha256 ||
            stored.QualificationCertificateId != qualified.CertificateId ||
            stored.QualificationCertificateSha256 != qualified.CertificateSha256 ||
            stored.QualificationStartedAtUtc != qualified.QualifiedAtUtc ||
            stored.StrategyId != qualified.StrategyId)
            return "qualification-lineage-mismatch";
        QualifiedPaperTradingSessionArtifact artifact;
        try
        {
            artifact = JsonSerializer.Deserialize<QualifiedPaperTradingSessionArtifact>(stored.ArtifactJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
                { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false) } })!;
        }
        catch (JsonException) { return "artifact-json-invalid"; }
        if (artifact is null || artifact.SchemaVersion != 2 ||
            !ProgressionArtifactIO.VerifyDisplayHash(artifact, artifact.ArtifactSha256,
                value => value with { ArtifactSha256 = string.Empty })) return "artifact-hash-invalid";
        if (artifact.SessionId != stored.Id || artifact.SessionId != artifact.Result.SessionId ||
            artifact.CreatedAtUtc != stored.CreatedAtUtc || artifact.Result.CreatedAtUtc != stored.CreatedAtUtc ||
            artifact.StrategyCertificateId != stored.StrategyCertificateId ||
            artifact.MarketFeedCaptureId != stored.MarketFeedCaptureId ||
            artifact.ConfigurationSha256 != stored.ConfigurationSha256 ||
            artifact.StrategyQualificationId != qualified.QualificationId ||
            artifact.StrategyQualificationSha256 != qualified.QualificationSha256 ||
            artifact.QualificationCertificateId != qualified.CertificateId ||
            artifact.QualificationCertificateSha256 != qualified.CertificateSha256 ||
            artifact.QualificationStartedAtUtc != qualified.QualifiedAtUtc ||
            artifact.Result.StrategyId != qualified.StrategyId || artifact.ArtifactSha256 != stored.ArtifactSha256 ||
            artifact.Result.InitialCash != stored.InitialCash || artifact.Result.EndingCash != stored.EndingCash ||
            artifact.Result.SubmittedOrders != stored.SubmittedOrders ||
            artifact.Result.FilledTrades != stored.FilledTrades ||
            artifact.Result.RejectedOrders != stored.RejectedOrders ||
            artifact.Result.RealizedNetPnl != stored.RealizedNetPnl) return "artifact-identity-mismatch";
        if (!artifact.OperatorApproved) return "operator-approval-missing";
        var zone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        var dates = artifact.Result.Trades.Select(item => DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(item.SubmittedAtUtc, zone))).Distinct().ToArray();
        if (dates.Length != 1) return "not-one-exchange-trading-date";
        verified = new(artifact.SessionId, artifact.CreatedAtUtc, dates[0], artifact.Result.StrategyId,
            artifact.ArtifactSha256, artifact.Result.SubmittedOrders, artifact.Result.FilledTrades,
            artifact.Result.RejectedOrders, artifact.Result.RealizedNetPnl);
        return null;
    }
}

public sealed record PaperQualificationFileInput(DateTime QualificationCutoffUtc);

public static class LiveReconciliationCommands
{
    public static bool IsCommand(string[] args) => args.Length > 0 &&
        args[0] is "reconcile-live" or "reconcile-live-file";
    public static bool IsAuthoritativeForDirect(LiveReconciliationArtifact value) =>
        LiveReconciliationEngine.Verify(value) && value.SchemaVersion == 2 &&
        value.Status == LiveReconciliationStatus.Reconciled && value.EligibleForControlledAutomation &&
        value.SourceMode == LiveReconciliationSourceMode.AuthoritativeBroker;

    public static Task<int> RunAsync(string[] args, IServiceProvider services,
        IConfiguration configuration, TextWriter output, TextWriter error,
        CancellationToken token = default) => args[0] == "reconcile-live-file" ?
        RunFileAsync(args, configuration, output, error, token) :
        RunAuthoritativeAsync(args, services, configuration, output, error, token);

    private static async Task<int> RunAuthoritativeAsync(string[] args, IServiceProvider services,
        IConfiguration configuration, TextWriter output, TextWriter error, CancellationToken token)
    {
        string? created = null;
        try
        {
            var command = ProgressionArtifactIO.Parse(args, "--paper-qualification", "--output");
            var paper = await ReadPaperAsync(command["--paper-qualification"], token);
            var policy = configuration.GetSection("LiveReconciliation").Get<LiveReconciliationPolicy>() ?? new();
            var reconciledAtUtc = DateTime.UtcNow;
            await using var scope = services.CreateAsyncScope();
            var broker = await scope.ServiceProvider.GetRequiredService<ILiveBrokerReconciliationClient>()
                .GetReconciliationStateAsync(token);
            var read = await scope.ServiceProvider.GetRequiredService<IInternalTradingLedgerReader>()
                .ReadAsync(paper.StrategyId, reconciledAtUtc, token);
            var internalState = ValidateInternalState(read, paper.StrategyId, broker,
                reconciledAtUtc, policy);
            var internalOrders = MapInternalOrders(internalState, read.LiveOrders);
            var brokerOrders = MapBrokerOrders(broker.Orders, internalOrders);
            var snapshot = new LiveReconciliationSnapshot(broker.AsOfUtc,
                internalState.ExpectedAvailableCash, broker.AvailableCash, policy.CashTolerance,
                internalState.Positions.Where(item => item.Quantity != 0).Select(item =>
                    new ReconciliationPosition(item.InstrumentToken, item.Exchange,
                        item.TradingSymbol, item.Product, item.Quantity)).ToArray(),
                broker.Positions.Where(item => item.Quantity != 0).Select(item =>
                    new ReconciliationPosition(item.InstrumentToken, item.Exchange,
                        item.TradingSymbol, item.Product, item.Quantity)).ToArray(),
                internalOrders, brokerOrders);
            var provenance = new LiveReconciliationProvenance(
                LiveReconciliationSourceMode.AuthoritativeBroker, broker.Provider, broker.AccountId,
                ProgressionArtifactIO.Digest(broker), internalState.ArtifactSha256,
                internalState.Revision);
            var artifact = LiveReconciliationEngine.Reconcile(paper.PaperQualificationId,
                paper.PaperQualificationSha256, paper.StrategyId, snapshot, reconciledAtUtc,
                policy, provenance);
            if (artifact.Status == LiveReconciliationStatus.Reconciled)
            {
                var india = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
                await scope.ServiceProvider.GetRequiredService<IReconciledExecutionStateStore>().AddAsync(new(
                    Guid.NewGuid(), broker.AsOfUtc,
                    DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(broker.AsOfUtc, india)),
                    internalState.ReconciledRealizedPnlToday, 0,
                    broker.Positions.Count(item => item.Quantity != 0), artifact.ReconciliationId,
                    artifact.ReconciliationSha256, internalState.Revision, true), token);
            }
            await ProgressionArtifactIO.WriteNewAsync(command["--output"],
                LiveReconciliationEngine.Serialize(artifact), token); created = command["--output"];
            await WriteResultAsync(artifact, command["--output"], output);
            created = null;
            return artifact.Status == LiveReconciliationStatus.Reconciled ? 0 : 4;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { ProgressionArtifactIO.Delete(created); await error.WriteLineAsync("Live reconciliation cancelled."); return 130; }
        catch (Exception exception) when (ProgressionArtifactIO.Expected(exception))
        { ProgressionArtifactIO.Delete(created); await error.WriteLineAsync(exception.Message); return 2; }
    }

    private static async Task<int> RunFileAsync(string[] args, IConfiguration configuration, TextWriter output,
        TextWriter error, CancellationToken token)
    {
        string? created = null;
        try
        {
            var command = ProgressionArtifactIO.Parse(args, "--paper-qualification", "--file", "--output");
            var paper = await ReadPaperAsync(command["--paper-qualification"], token);
            var input = await ProgressionArtifactIO.ReadAsync<LiveReconciliationFileInput>(command["--file"], token);
            var baseDirectory = Path.GetDirectoryName(command["--file"])!;
            var internalOrders = new List<ReconciliationOrder>();
            foreach (var name in input.LiveOrderArtifactFiles ?? [])
            {
                var path = ProgressionArtifactIO.ResolveReferencedJson(baseDirectory, name);
                var order = await ProgressionArtifactIO.ReadAsync<LiveOrderArtifact>(path, token);
                if (!ProgressionArtifactIO.VerifyDisplayHash(order, order.ArtifactSha256,
                        value => value with { ArtifactSha256 = string.Empty }) ||
                    order.Proposal.StrategyId != paper.StrategyId || order.BrokerReceipt is null ||
                    order.Status != "Submitted")
                    throw new InvalidDataException($"Live order '{name}' is not a verified submitted M23 order for this strategy.");
                internalOrders.Add(new(order.Proposal.RequestId, order.BrokerReceipt.BrokerOrderId,
                    order.Proposal.InstrumentToken, order.Proposal.Exchange, order.Proposal.TradingSymbol,
                    ReconciledOrderStatus.Submitted, order.Proposal.Quantity, 0, null));
            }
            if (input.InternalOrders is { Count: > 0 })
            {
                if (input.InternalOrders.Count != internalOrders.Count || input.InternalOrders.Any(current =>
                    !internalOrders.Any(source => source.RequestId == current.RequestId &&
                        source.BrokerOrderId == current.BrokerOrderId &&
                        source.InstrumentToken == current.InstrumentToken && source.Exchange == current.Exchange &&
                        source.TradingSymbol == current.TradingSymbol &&
                        source.OrderedQuantity == current.OrderedQuantity)))
                    throw new InvalidDataException("Internal order state must be identity-bound to every supplied M23 order artifact.");
                internalOrders = input.InternalOrders.ToList();
            }
            var snapshot = new LiveReconciliationSnapshot(input.AsOfUtc, input.ExpectedAvailableCash,
                input.BrokerAvailableCash, input.CashTolerance, input.ExpectedPositions ?? [],
                input.BrokerPositions ?? [], internalOrders, input.BrokerOrders ?? []);
            var policy = configuration.GetSection("LiveReconciliation").Get<LiveReconciliationPolicy>() ?? new();
            var provenance = new LiveReconciliationProvenance(LiveReconciliationSourceMode.DiagnosticFile,
                "manual-json", "unverified", ProgressionArtifactIO.Digest(new
                { input.AsOfUtc, input.BrokerAvailableCash, input.BrokerPositions, input.BrokerOrders }),
                ProgressionArtifactIO.Digest(new { input.ExpectedAvailableCash, input.ExpectedPositions,
                    internalOrders }), "diagnostic-file");
            var artifact = LiveReconciliationEngine.Reconcile(paper.PaperQualificationId,
                paper.PaperQualificationSha256, paper.StrategyId, snapshot, DateTime.UtcNow, policy, provenance);
            await ProgressionArtifactIO.WriteNewAsync(command["--output"],
                LiveReconciliationEngine.Serialize(artifact), token); created = command["--output"];
            await WriteResultAsync(artifact, command["--output"], output);
            created = null; return artifact.Status == LiveReconciliationStatus.Reconciled ? 0 : 4;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { ProgressionArtifactIO.Delete(created); await error.WriteLineAsync("Live reconciliation cancelled."); return 130; }
        catch (Exception exception) when (ProgressionArtifactIO.Expected(exception))
        { ProgressionArtifactIO.Delete(created); await error.WriteLineAsync(exception.Message); return 2; }
    }

    private static async Task<PaperQualificationArtifact> ReadPaperAsync(string path, CancellationToken token)
    {
        var paper = await ProgressionArtifactIO.ReadAsync<PaperQualificationArtifact>(path, token);
        if (!PaperQualificationEngine.Verify(paper) || paper.SchemaVersion != 2 ||
            !paper.EligibleForLiveReconciliation || paper.Status != PaperQualificationStatus.Qualified ||
            DateTime.UtcNow >= paper.ExpiresAtUtc)
            throw new InvalidDataException("The M35 paper qualification is invalid, expired, or not eligible for reconciliation.");
        return paper;
    }

    private static InternalTradingLedgerArtifact ValidateInternalState(InternalTradingLedgerRead read,
        string strategyId, BrokerReconciliationState broker, DateTime nowUtc, LiveReconciliationPolicy policy)
    {
        var state = read.State ?? throw new InvalidDataException("No durable internal trading-ledger state is available.");
        if (!InternalTradingLedgerCodec.Verify(state) || !state.Valid || state.UnresolvedEvents != 0 ||
            state.StrategyId != strategyId || state.BrokerProvider != broker.Provider ||
            state.BrokerAccountId != broker.AccountId || state.AsOfUtc > nowUtc ||
            nowUtc - state.AsOfUtc > TimeSpan.FromSeconds(policy.MaximumInternalStateAgeSeconds))
            throw new InvalidDataException("The internal trading ledger is invalid, stale, unresolved, or for another broker account.");
        if (broker.AsOfUtc.Kind != DateTimeKind.Utc || broker.AsOfUtc > nowUtc ||
            nowUtc - broker.AsOfUtc > TimeSpan.FromSeconds(policy.MaximumSnapshotAgeSeconds) ||
            string.IsNullOrWhiteSpace(broker.Provider) || string.IsNullOrWhiteSpace(broker.AccountId))
            throw new InvalidDataException("The broker reconciliation snapshot is stale or lacks account provenance.");
        return state;
    }

    private static IReadOnlyList<ReconciliationOrder> MapInternalOrders(InternalTradingLedgerArtifact state,
        IReadOnlyList<DurableLiveOrderEvidence> records)
    {
        if (records.Any(item => item.Status != "Submitted" || string.IsNullOrWhiteSpace(item.BrokerOrderId)))
            throw new InvalidDataException("The durable live-order ledger contains an unresolved submission.");
        var mapped = state.Orders.Select(item => new ReconciliationOrder(item.RequestId, item.BrokerOrderId,
            item.InstrumentToken, item.Exchange, item.TradingSymbol, Status(item.Status),
            item.OrderedQuantity, item.FilledQuantity, item.AverageFillPrice)).ToArray();
        if (mapped.Length != records.Count) throw new InvalidDataException("Internal order events do not cover every persisted live order.");
        foreach (var record in records)
        {
            var artifact = ProgressionArtifactIO.Deserialize<LiveOrderArtifact>(record.ArtifactJson);
            if (!ProgressionArtifactIO.VerifyDisplayHash(artifact, artifact.ArtifactSha256,
                    value => value with { ArtifactSha256 = string.Empty }) || artifact.SchemaVersion != 2 ||
                artifact.Status != "Submitted" || artifact.BrokerReceipt is null || artifact.LiveOrderId != record.Id ||
                artifact.Proposal.RequestId != record.RequestId || artifact.Proposal.StrategyId != record.StrategyId ||
                artifact.Proposal.Exchange != record.Exchange || artifact.Proposal.TradingSymbol != record.TradingSymbol ||
                artifact.Proposal.Quantity != record.Quantity || artifact.BrokerReceipt.BrokerOrderId != record.BrokerOrderId ||
                artifact.ArtifactSha256 != record.ArtifactSha256)
                throw new InvalidDataException("A persisted M23 live-order artifact or broker receipt is invalid.");
            var ledger = mapped.SingleOrDefault(item => item.BrokerOrderId == record.BrokerOrderId);
            if (ledger is null || ledger.RequestId != record.RequestId ||
                ledger.InstrumentToken != artifact.Proposal.InstrumentToken || ledger.Exchange != record.Exchange ||
                ledger.TradingSymbol != record.TradingSymbol || ledger.OrderedQuantity != record.Quantity)
                throw new InvalidDataException("A persisted live order cannot be mapped to internal fill/order events.");
        }
        return mapped;
    }

    private static IReadOnlyList<ReconciliationOrder> MapBrokerOrders(
        IReadOnlyList<BrokerReconciliationOrder> brokerOrders, IReadOnlyList<ReconciliationOrder> internalOrders) =>
        brokerOrders.Select(item =>
        {
            var known = internalOrders.SingleOrDefault(order => order.BrokerOrderId == item.BrokerOrderId);
            return new ReconciliationOrder(known?.RequestId ?? DeterministicId(item.BrokerOrderId),
                item.BrokerOrderId, item.InstrumentToken, item.Exchange, item.TradingSymbol,
                Status(item.Status), item.OrderedQuantity, item.FilledQuantity, item.AverageFillPrice);
        }).ToArray();

    private static ReconciledOrderStatus Status(string value) => value.Replace("_", string.Empty,
        StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant() switch
    {
        "PENDING" => ReconciledOrderStatus.Pending,
        "SUBMITTED" or "OPEN" => ReconciledOrderStatus.Submitted,
        "PARTIAL" or "PARTIALLYFILLED" => ReconciledOrderStatus.PartiallyFilled,
        "FILLED" or "COMPLETE" => ReconciledOrderStatus.Filled,
        "CANCELLED" or "CANCELED" => ReconciledOrderStatus.Cancelled,
        "REJECTED" => ReconciledOrderStatus.Rejected,
        _ => throw new InvalidDataException($"Order status '{value}' cannot be mapped deterministically.")
    };
    private static Guid DeterministicId(string value) => new(System.Security.Cryptography.SHA256.HashData(
        Encoding.UTF8.GetBytes(value))[..16]);
    private static Task WriteResultAsync(LiveReconciliationArtifact artifact, string path, TextWriter output) =>
        output.WriteLineAsync(ProgressionArtifactIO.Serialize(new { status = artifact.Status,
            artifact.SourceMode, artifact.ReconciliationId, artifact.StrategyId, artifact.CashDifference,
            artifact.DiscrepancyCodes, output = path, artifact.ReconciliationSha256 }));
}

public sealed record LiveReconciliationFileInput(DateTime AsOfUtc, decimal ExpectedAvailableCash,
    decimal BrokerAvailableCash, decimal CashTolerance, IReadOnlyList<ReconciliationPosition>? ExpectedPositions,
    IReadOnlyList<ReconciliationPosition>? BrokerPositions, IReadOnlyList<string>? LiveOrderArtifactFiles,
    IReadOnlyList<ReconciliationOrder>? BrokerOrders, IReadOnlyList<ReconciliationOrder>? InternalOrders);

public static class ControlledAutomationCommands
{
    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] == "evaluate-automation";

    public static async Task<int> RunAsync(string[] args, IServiceProvider services,
        IConfiguration configuration, TextWriter output,
        TextWriter error, CancellationToken token = default)
    {
        string? created = null;
        try
        {
            var command = ProgressionArtifactIO.Parse(args, "--qualified-strategy", "--paper-qualification",
                "--reconciliation", "--file", "--output");
            var qualified = await ProgressionArtifactIO.ReadAsync<QualifiedStrategyArtifact>(command["--qualified-strategy"], token);
            var paper = await ProgressionArtifactIO.ReadAsync<PaperQualificationArtifact>(command["--paper-qualification"], token);
            var reconciliation = await ProgressionArtifactIO.ReadAsync<LiveReconciliationArtifact>(command["--reconciliation"], token);
            var intent = await ProgressionArtifactIO.ReadAsync<ControlledAutomationIntent>(command["--file"], token);
            if (!QualifiedStrategyPipeline.Verify(qualified) || !PaperQualificationEngine.Verify(paper) ||
                paper.SchemaVersion != 2 || !LiveReconciliationEngine.Verify(reconciliation) ||
                DateTime.UtcNow >= qualified.ExpiresAtUtc ||
                paper.Status != PaperQualificationStatus.Qualified ||
                reconciliation.Status != LiveReconciliationStatus.Reconciled ||
                paper.StrategyQualificationId != qualified.QualificationId ||
                paper.StrategyQualificationSha256 != qualified.QualificationSha256 ||
                reconciliation.PaperQualificationId != paper.PaperQualificationId ||
                reconciliation.PaperQualificationSha256 != paper.PaperQualificationSha256 ||
                paper.StrategyId != qualified.StrategyId || reconciliation.StrategyId != qualified.StrategyId ||
                (intent.Mode == ControlledAutomationMode.DirectLive &&
                    !LiveReconciliationCommands.IsAuthoritativeForDirect(reconciliation)))
                throw new InvalidDataException("M34, M35, and M36 artifacts are invalid, expired, divergent, or do not form one evidence chain.");
            var settings = configuration.GetSection("ControlledAutomation").Get<ControlledAutomationSettings>() ?? new();
            await using var scope = services.CreateAsyncScope();
            var evaluatedAtUtc = DateTime.UtcNow;
            var state = await scope.ServiceProvider.GetRequiredService<IControlledAutomationStateProvider>()
                .GetAsync(evaluatedAtUtc, token);
            var artifact = ControlledAutomationEngine.Evaluate(qualified.QualificationId,
                qualified.QualificationSha256, paper.PaperQualificationId, paper.PaperQualificationSha256,
                reconciliation.ReconciliationId, reconciliation.ReconciliationSha256, qualified.StrategyId,
                reconciliation.ReconciledAtUtc, intent, state, evaluatedAtUtc, settings);
            await ProgressionArtifactIO.WriteNewAsync(command["--output"],
                ControlledAutomationEngine.Serialize(artifact), token); created = command["--output"];
            await output.WriteLineAsync(ProgressionArtifactIO.Serialize(new { decision = artifact.Decision,
                artifact.AutomationDecisionId, artifact.StrategyId, artifact.BlockCodes,
                artifact.MaximumAuthorizedActions, artifact.ExpiresAtUtc, artifact.BrokerSubmissionPerformed,
                output = command["--output"], artifact.AutomationSha256 }));
            created = null; return artifact.Decision == ControlledAutomationDecision.Blocked ? 4 : 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { ProgressionArtifactIO.Delete(created); await error.WriteLineAsync("Controlled automation evaluation cancelled."); return 130; }
        catch (Exception exception) when (ProgressionArtifactIO.Expected(exception))
        { ProgressionArtifactIO.Delete(created); await error.WriteLineAsync(exception.Message); return 2; }
    }
}

internal static class ProgressionArtifactIO
{
    private const int MaximumBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = CreateOptions();

    public static Dictionary<string, string> Parse(string[] args, params string[] required)
    {
        var allowed = required.ToHashSet(StringComparer.Ordinal); var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length) throw new ArgumentException($"Missing value for {args[i]}.");
            if (!allowed.Contains(args[i]) || !values.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException($"Unknown or duplicate option: {args[i]}");
        }
        foreach (var option in required)
            if (!values.ContainsKey(option)) throw new ArgumentException($"{option} is required.");
        foreach (var option in required) values[option] = JsonPath(values[option], option, option != "--output");
        if (File.Exists(values["--output"])) throw new IOException("The output file already exists.");
        return values;
    }

    public static async Task<T> ReadAsync<T>(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length > MaximumBytes) throw new ArgumentException($"Input '{Path.GetFileName(path)}' exceeds 8 MiB.");
        return await JsonSerializer.DeserializeAsync<T>(stream, Json, token) ??
            throw new InvalidDataException($"Input '{Path.GetFileName(path)}' is empty or invalid.");
    }

    public static bool VerifyDisplayHash<T>(T value, string hash, Func<T, T> unsigned) =>
        IsHash(hash) && Sha256(JsonSerializer.Serialize(unsigned(value), Json)) == hash.ToLowerInvariant();

    public static string ResolveReferencedJson(string baseDirectory, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A referenced artifact path is empty.");
        var path = Path.GetFullPath(value, baseDirectory);
        if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("A referenced JSON artifact was not found.", path);
        return path;
    }

    public static async Task WriteNewAsync(string path, string value, CancellationToken token)
    {
        var temp = $"{path}.tmp-{Guid.NewGuid():N}";
        try { await File.WriteAllTextAsync(temp, value, new UTF8Encoding(false), token); File.Move(temp, path, false); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, Json) ??
        throw new InvalidDataException("Stored artifact JSON is empty or invalid.");
    public static string Digest<T>(T value) => Sha256(JsonSerializer.Serialize(value, Json));
    public static bool Expected(Exception value) => value is ArgumentException or FormatException or IOException or
        InvalidDataException or UnauthorizedAccessException or InvalidOperationException or JsonException or
        HttpRequestException;
    public static void Delete(string? path) { if (path is not null && File.Exists(path)) File.Delete(path); }

    private static string JsonPath(string value, string option, bool mustExist)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{option} is required.");
        var path = Path.GetFullPath(value);
        if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{option} must use .json.");
        if (mustExist && !File.Exists(path)) throw new FileNotFoundException($"The {option} file was not found.", path);
        if (!mustExist && !Directory.Exists(Path.GetDirectoryName(path))) throw new ArgumentException("The output directory does not exist.");
        return path;
    }
    private static bool IsHash(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static JsonSerializerOptions CreateOptions()
    { var value = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
      value.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false)); return value; }
}
