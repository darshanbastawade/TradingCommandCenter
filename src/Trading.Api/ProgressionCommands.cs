using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trading.Backtesting.Certification;
using Trading.Execution.Automation;
using Trading.Execution.Qualification;
using Trading.Execution.Reconciliation;

namespace Trading.Api;

public static class PaperQualificationCommands
{
    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] == "qualify-paper";

    public static async Task<int> RunAsync(string[] args, IConfiguration configuration, TextWriter output,
        TextWriter error, CancellationToken token = default)
    {
        string? created = null;
        try
        {
            var command = ProgressionArtifactIO.Parse(args, "--qualified-strategy", "--file", "--output");
            var qualified = await ProgressionArtifactIO.ReadAsync<QualifiedStrategyArtifact>(command["--qualified-strategy"], token);
            if (!QualifiedStrategyPipeline.Verify(qualified) || !qualified.EligibleForPaperQualification ||
                qualified.SemiLiveAuthorized || qualified.DirectLiveAuthorized || DateTime.UtcNow >= qualified.ExpiresAtUtc)
                throw new InvalidDataException("The M34 qualified strategy is invalid, expired, or not eligible for paper qualification.");
            var input = await ProgressionArtifactIO.ReadAsync<PaperQualificationFileInput>(command["--file"], token);
            if (input.SessionFiles is null || input.SessionFiles.Count == 0)
                throw new ArgumentException("At least one M22 paper-session file is required.");
            var baseDirectory = Path.GetDirectoryName(command["--file"])!;
            var sessions = new List<VerifiedPaperSession>();
            foreach (var name in input.SessionFiles)
            {
                var path = ProgressionArtifactIO.ResolveReferencedJson(baseDirectory, name);
                var session = await ProgressionArtifactIO.ReadAsync<PaperTradingSessionArtifact>(path, token);
                if (!ProgressionArtifactIO.VerifyDisplayHash(session, session.ArtifactSha256,
                        value => value with { ArtifactSha256 = string.Empty }) ||
                    session.SchemaVersion != 1 || session.SessionId != session.Result.SessionId ||
                    session.Result.StrategyId != qualified.StrategyId || !session.OperatorApproved)
                    throw new InvalidDataException($"Paper session '{name}' has an invalid hash, identity, strategy, or approval.");
                var tradingDates = session.Result.Trades.Select(item => DateOnly.FromDateTime(
                    TimeZoneInfo.ConvertTimeFromUtc(item.SubmittedAtUtc,
                        TimeZoneInfo.FindSystemTimeZoneById("India Standard Time")))).Distinct().ToArray();
                if (tradingDates.Length != 1)
                    throw new InvalidDataException($"Paper session '{name}' must contain orders from exactly one trading date.");
                sessions.Add(new(session.SessionId, session.CreatedAtUtc, tradingDates[0], session.Result.StrategyId,
                    session.ArtifactSha256, session.Result.SubmittedOrders, session.Result.FilledTrades,
                    session.Result.RejectedOrders, session.Result.RealizedNetPnl));
            }
            var policy = configuration.GetSection("PaperQualification").Get<PaperQualificationPolicy>() ?? new();
            var artifact = PaperQualificationEngine.Evaluate(qualified.QualificationId,
                qualified.QualificationSha256, qualified.CertificateId, qualified.CertificateSha256,
                qualified.StrategyId, qualified.ExpiresAtUtc, sessions, DateTime.UtcNow, policy);
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
}

public sealed record PaperQualificationFileInput(IReadOnlyList<string> SessionFiles);

public static class LiveReconciliationCommands
{
    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] == "reconcile-live";

    public static async Task<int> RunAsync(string[] args, IConfiguration configuration, TextWriter output,
        TextWriter error, CancellationToken token = default)
    {
        string? created = null;
        try
        {
            var command = ProgressionArtifactIO.Parse(args, "--paper-qualification", "--file", "--output");
            var paper = await ProgressionArtifactIO.ReadAsync<PaperQualificationArtifact>(command["--paper-qualification"], token);
            if (!PaperQualificationEngine.Verify(paper) || !paper.EligibleForLiveReconciliation ||
                paper.Status != PaperQualificationStatus.Qualified || DateTime.UtcNow >= paper.ExpiresAtUtc)
                throw new InvalidDataException("The M35 paper qualification is invalid, expired, or not eligible for reconciliation.");
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
            var artifact = LiveReconciliationEngine.Reconcile(paper.PaperQualificationId,
                paper.PaperQualificationSha256, paper.StrategyId, snapshot, DateTime.UtcNow, policy);
            await ProgressionArtifactIO.WriteNewAsync(command["--output"],
                LiveReconciliationEngine.Serialize(artifact), token); created = command["--output"];
            await output.WriteLineAsync(ProgressionArtifactIO.Serialize(new { status = artifact.Status,
                artifact.ReconciliationId, artifact.StrategyId, artifact.CashDifference,
                artifact.DiscrepancyCodes, output = command["--output"], artifact.ReconciliationSha256 }));
            created = null; return artifact.Status == LiveReconciliationStatus.Reconciled ? 0 : 4;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { ProgressionArtifactIO.Delete(created); await error.WriteLineAsync("Live reconciliation cancelled."); return 130; }
        catch (Exception exception) when (ProgressionArtifactIO.Expected(exception))
        { ProgressionArtifactIO.Delete(created); await error.WriteLineAsync(exception.Message); return 2; }
    }
}

public sealed record LiveReconciliationFileInput(DateTime AsOfUtc, decimal ExpectedAvailableCash,
    decimal BrokerAvailableCash, decimal CashTolerance, IReadOnlyList<ReconciliationPosition>? ExpectedPositions,
    IReadOnlyList<ReconciliationPosition>? BrokerPositions, IReadOnlyList<string>? LiveOrderArtifactFiles,
    IReadOnlyList<ReconciliationOrder>? BrokerOrders, IReadOnlyList<ReconciliationOrder>? InternalOrders);

public static class ControlledAutomationCommands
{
    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] == "evaluate-automation";

    public static async Task<int> RunAsync(string[] args, IConfiguration configuration, TextWriter output,
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
                !LiveReconciliationEngine.Verify(reconciliation) || DateTime.UtcNow >= qualified.ExpiresAtUtc ||
                paper.Status != PaperQualificationStatus.Qualified ||
                reconciliation.Status != LiveReconciliationStatus.Reconciled ||
                paper.StrategyQualificationId != qualified.QualificationId ||
                paper.StrategyQualificationSha256 != qualified.QualificationSha256 ||
                reconciliation.PaperQualificationId != paper.PaperQualificationId ||
                reconciliation.PaperQualificationSha256 != paper.PaperQualificationSha256 ||
                paper.StrategyId != qualified.StrategyId || reconciliation.StrategyId != qualified.StrategyId)
                throw new InvalidDataException("M34, M35, and M36 artifacts are invalid, expired, divergent, or do not form one evidence chain.");
            var settings = configuration.GetSection("ControlledAutomation").Get<ControlledAutomationSettings>() ?? new();
            var artifact = ControlledAutomationEngine.Evaluate(qualified.QualificationId,
                qualified.QualificationSha256, paper.PaperQualificationId, paper.PaperQualificationSha256,
                reconciliation.ReconciliationId, reconciliation.ReconciliationSha256, qualified.StrategyId,
                reconciliation.ReconciledAtUtc, intent, DateTime.UtcNow, settings);
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
    public static bool Expected(Exception value) => value is ArgumentException or FormatException or IOException or
        UnauthorizedAccessException or InvalidOperationException or JsonException;
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
