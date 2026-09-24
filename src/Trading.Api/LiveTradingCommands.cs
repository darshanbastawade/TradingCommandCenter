using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trading.Application.Execution;
using Trading.Application.Research;
using Trading.Backtesting.Certification;
using Trading.Domain.Execution;
using Trading.Execution.Live;
using Trading.Execution.Paper;
using Trading.Execution.Automation;
using Trading.Execution.Zerodha;
using Trading.Risk.Policy;

namespace Trading.Api;

public static class LiveTradingCommands
{
    private static readonly JsonSerializerOptions Json = Options();
    private static readonly JsonSerializerOptions CertificateHashJson = new(JsonSerializerDefaults.Web);
    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] == "live-order";

    public static async Task<int> RunAsync(string[] args, IServiceProvider services, IConfiguration configuration,
        TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        string? createdFile = null; var brokerAttempted = false;
        try
        {
            var command = Parse(args);
            var intent = await ReadInputAsync(command.Input, cancellationToken);
            var nowUtc = DateTime.UtcNow;
            var authorization = command.Mode == LiveTradingMode.DirectLive
                ? await ReadAndVerifyAuthorizationAsync(command.AutomationAuthorization, intent, nowUtc,
                    cancellationToken)
                : null;
            var liveSettings = configuration.GetSection("LiveTrading").Get<LiveTradingSettings>() ?? new();
            var zerodha = configuration.GetSection(ZerodhaFeedOptions.SectionName).Get<ZerodhaFeedOptions>() ?? new();
            if (command.Mode == LiveTradingMode.DirectLive && (!liveSettings.AllowDirectOrders || !zerodha.AllowLiveOrders))
                throw new InvalidOperationException("Direct live orders are disabled by both LiveTrading and Zerodha configuration gates.");
            if (command.Mode == LiveTradingMode.DirectLive &&
                (!intent.OperatorApproved || command.Confirmation != "PLACE-LIVE-ORDER"))
                throw new InvalidOperationException("Direct mode requires operatorApproved=true and --confirm PLACE-LIVE-ORDER.");

            await using var scope = services.CreateAsyncScope();
            var certificateEntity = await scope.ServiceProvider.GetRequiredService<IStrategyCertificateStore>()
                .FindAsync(command.CertificateId, cancellationToken) ?? throw new ArgumentException("The strategy certificate was not found.");
            var certificate = VerifyCertificate(certificateEntity, DateTime.UtcNow);
            if (authorization is not null && authorization.StrategyId != certificate.StrategyId)
                throw new InvalidDataException("The M37 authorization strategy does not match the certified strategy.");
            var paper = await VerifiedPaperEvidenceAsync(scope.ServiceProvider, certificate, liveSettings, cancellationToken);
            var broker = scope.ServiceProvider.GetRequiredService<ILiveBrokerClient>();
            var account = await broker.GetAccountSnapshotAsync(cancellationToken);
            var quote = await broker.GetQuoteAsync(intent.InstrumentToken, intent.Exchange,
                intent.TradingSymbol, cancellationToken);
            var evaluatedAt = DateTime.UtcNow;
            var risk = configuration.GetSection("RiskPolicy").Get<DeterministicRiskPolicySettings>() ?? new();
            var effectiveRisk = risk with
            {
                MaximumRiskPerTrade = decimal.Min(risk.MaximumRiskPerTrade, certificate.Constraints.AllowedRisk)
            };
            var constrainedIntent = intent with { MaximumLots = Minimum(intent.MaximumLots, certificate.Constraints.MaximumLots) };
            var calendar = await ExchangeCalendarLoader.LoadAsync(configuration, cancellationToken);
            var proposal = LiveTradingEngine.Prepare(effectiveRisk, liveSettings, account, quote,
                constrainedIntent, certificate.StrategyId, evaluatedAt, calendar);
            var id = Guid.NewGuid(); var createdAt = DateTime.UtcNow;
            var status = command.Mode == LiveTradingMode.SemiLive ? "Proposed" : "Prepared";
            var unsigned = new LiveOrderArtifact(command.Mode == LiveTradingMode.DirectLive ? 2 : 1,
                id, createdAt, command.Mode, status,
                certificate.CertificateId, certificate.CertificateSha256, paper.SessionEvidence,
                paper.EvidenceSha256, account, quote, proposal, null, intent.OperatorApproved, string.Empty,
                authorization?.AutomationDecisionId, authorization?.AutomationSha256,
                authorization?.StrategyQualificationId, authorization?.PaperQualificationId,
                authorization?.ReconciliationId);
            var preparedHash = Sha256(JsonSerializer.Serialize(unsigned, Json));
            var prepared = unsigned with { ArtifactSha256 = preparedHash };
            var preparedJson = JsonSerializer.Serialize(prepared, Json);
            var record = new LiveOrderRecord(id, certificate.CertificateId, intent.RequestId, createdAt,
                Mode(command.Mode), status, certificate.StrategyId, intent.Exchange, intent.TradingSymbol,
                proposal.Quantity, intent.LimitPrice, intent.StopPrice, intent.TargetPrice,
                paper.EvidenceSha256, proposal.RiskDecisionSha256, string.Empty, preparedHash, preparedJson);
            var store = scope.ServiceProvider.GetRequiredService<ILiveOrderStore>();
            await store.AddAsync(record, cancellationToken);
            await WriteNewAtomicallyAsync(command.Output, preparedJson, cancellationToken); createdFile = command.Output;

            BrokerOrderReceipt? receipt = null;
            if (command.Mode == LiveTradingMode.DirectLive)
            {
                var consumed = await scope.ServiceProvider
                    .GetRequiredService<IControlledAutomationAuthorizationStore>().TryConsumeAsync(new(
                        authorization!.AutomationDecisionId, authorization.AutomationSha256,
                        authorization.ActionId, authorization.ActionReference, authorization.StrategyId,
                        authorization.EvaluatedAtUtc, authorization.ExpiresAtUtc,
                        authorization.Decision.ToString(), authorization.MaximumAuthorizedActions, id),
                        DateTime.UtcNow, cancellationToken);
                if (!consumed)
                    throw new InvalidOperationException("The M37 authorization is already consumed or could not be reserved atomically.");
                brokerAttempted = true;
                receipt = await broker.PlaceLimitBuyAsync(new(intent.RequestId, intent.Exchange,
                    intent.TradingSymbol, proposal.Quantity, intent.LimitPrice, "MIS",
                    $"tcc{intent.RequestId:N}"[..19]), cancellationToken);
                var submittedUnsigned = unsigned with { Status = "Submitted", BrokerReceipt = receipt };
                var submittedHash = Sha256(JsonSerializer.Serialize(submittedUnsigned, Json));
                var submitted = submittedUnsigned with { ArtifactSha256 = submittedHash };
                var submittedJson = JsonSerializer.Serialize(submitted, Json);
                record.MarkSubmitted(receipt.BrokerOrderId, submittedHash, submittedJson);
                await store.UpdateAsync(record, cancellationToken);
                await ReplaceAtomicallyAsync(command.Output, submittedJson, cancellationToken);
            }
            createdFile = null;
            await output.WriteLineAsync(JsonSerializer.Serialize(new { status = command.Mode == LiveTradingMode.SemiLive
                    ? "semi-live-proposal-created" : "live-order-submitted", liveOrderId = id,
                requestId = intent.RequestId, strategyId = certificate.StrategyId, proposal.Quantity,
                proposal.LimitPrice, brokerOrderId = receipt?.BrokerOrderId, output = command.Output }, Json));
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !brokerAttempted)
        { Delete(createdFile); await error.WriteLineAsync("Live-order preparation cancelled before broker submission."); return 130; }
        catch (Exception) when (brokerAttempted)
        {
            await error.WriteLineAsync("Broker submission may have occurred. Do not retry this request ID; reconcile the prepared record with Zerodha.");
            return 4;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or IOException or InvalidDataException or
                                           UnauthorizedAccessException or InvalidOperationException or JsonException or HttpRequestException)
        { Delete(createdFile); await error.WriteLineAsync(exception.Message); return 2; }
        catch (Exception)
        { Delete(createdFile); await error.WriteLineAsync("Live-order preparation failed before broker submission."); return 3; }
    }

    private static async Task<PaperEvidence> VerifiedPaperEvidenceAsync(IServiceProvider services,
        StrategyCertificate certificate, LiveTradingSettings settings, CancellationToken token)
    {
        if (settings.MinimumPaperSessions < 1 || settings.MinimumPaperFilledTrades < 1)
            throw new InvalidOperationException("Live-trading paper-evidence thresholds must be positive.");
        var sessions = await services.GetRequiredService<IPaperTradingSessionStore>()
            .ListForCertificateAsync(certificate.CertificateId, token);
        var evidence = new List<PaperSessionEvidence>(); var filled = 0; decimal net = 0;
        foreach (var session in sessions)
        {
            using var document = JsonDocument.Parse(session.ArtifactJson);
            var schema = document.RootElement.GetProperty("schemaVersion").GetInt32();
            Guid sessionId; Guid certificateId; string certificateHash; string artifactHash;
            PaperTradingResult result; string hash;
            if (schema == 1)
            {
                var artifact = JsonSerializer.Deserialize<PaperTradingSessionArtifact>(session.ArtifactJson, Json) ??
                    throw new InvalidDataException("A stored paper session is invalid.");
                sessionId = artifact.SessionId; certificateId = artifact.StrategyCertificateId;
                certificateHash = artifact.StrategyCertificateSha256; artifactHash = artifact.ArtifactSha256;
                result = artifact.Result;
                hash = Sha256(JsonSerializer.Serialize(artifact with { ArtifactSha256 = string.Empty }, Json));
            }
            else if (schema == 2)
            {
                var artifact = JsonSerializer.Deserialize<QualifiedPaperTradingSessionArtifact>(session.ArtifactJson, Json) ??
                    throw new InvalidDataException("A stored qualified paper session is invalid.");
                sessionId = artifact.SessionId; certificateId = artifact.StrategyCertificateId;
                certificateHash = artifact.StrategyCertificateSha256; artifactHash = artifact.ArtifactSha256;
                result = artifact.Result;
                hash = Sha256(JsonSerializer.Serialize(artifact with { ArtifactSha256 = string.Empty }, Json));
            }
            else throw new InvalidDataException("A stored paper session has an unsupported schema.");
            if (sessionId != session.Id || certificateId != certificate.CertificateId ||
                certificateHash != certificate.CertificateSha256 ||
                hash != session.ArtifactSha256 || artifactHash != session.ArtifactSha256)
                throw new InvalidDataException("A paper-session identity or hash is invalid.");
            evidence.Add(new(session.Id, session.ArtifactSha256, result.FilledTrades,
                result.RealizedNetPnl));
            filled += result.FilledTrades; net += result.RealizedNetPnl;
        }
        if (sessions.Count < settings.MinimumPaperSessions || filled < settings.MinimumPaperFilledTrades ||
            (settings.RequirePositivePaperNetPnl && net <= 0))
            throw new InvalidOperationException("Paper evidence does not meet the configured live-trading threshold.");
        var ordered = evidence.OrderBy(item => item.SessionId).ToArray();
        var evidenceHash = Sha256(string.Join('|', ordered.Select(item => $"{item.SessionId:D}:{item.ArtifactSha256}")));
        return new(ordered, evidenceHash);
    }

    private static async Task<ControlledAutomationArtifact> ReadAndVerifyAuthorizationAsync(string? path,
        LiveEntryIntent intent, DateTime nowUtc, CancellationToken token)
    {
        if (path is null)
            throw new ArgumentException("Direct mode requires --automation-authorization with a verified M37 artifact.");
        await using var stream = File.OpenRead(path);
        if (stream.Length > 1024 * 1024) throw new ArgumentException("The M37 authorization exceeds 1 MiB.");
        var value = await JsonSerializer.DeserializeAsync<ControlledAutomationArtifact>(stream, Json, token) ??
            throw new InvalidDataException("The M37 authorization is empty or invalid.");
        if (!ControlledAutomationEngine.Verify(value) || value.SchemaVersion != 2)
            throw new InvalidDataException("The M37 authorization SHA-256 or schema is invalid.");
        if (value.Decision != ControlledAutomationDecision.DirectSubmissionEligible ||
            value.BrokerSubmissionPerformed || value.MaximumAuthorizedActions != 1)
            throw new InvalidDataException("The M37 artifact does not authorize one direct submission.");
        if (nowUtc < value.EvaluatedAtUtc || nowUtc >= value.ExpiresAtUtc)
            throw new InvalidDataException("The M37 authorization is not currently valid.");
        if (value.ActionId != intent.RequestId)
            throw new InvalidDataException("The M37 action ID does not match the live request ID.");
        if (string.IsNullOrWhiteSpace(intent.AutomationActionReference) ||
            value.ActionReference != intent.AutomationActionReference.Trim())
            throw new InvalidDataException("The M37 action reference does not match the live intent.");
        return value;
    }

    private static StrategyCertificate VerifyCertificate(Trading.Domain.Research.IssuedStrategyCertificate entity,
        DateTime nowUtc)
    {
        var value = JsonSerializer.Deserialize<StrategyCertificate>(entity.CertificateJson, Json) ??
            throw new InvalidDataException("The strategy certificate is invalid.");
        var hash = Sha256(JsonSerializer.Serialize(value with { CertificateSha256 = string.Empty }, CertificateHashJson));
        if (value.CertificateId != entity.Id || value.ResearchRunId != entity.ResearchRunId ||
            value.CertificateSha256 != entity.CertificateSha256 || value.StrategyId != entity.StrategyId ||
            value.IssuedAtUtc != entity.IssuedAtUtc || value.ExpiresAtUtc != entity.ExpiresAtUtc ||
            value.ResearchArtifactSha256 != entity.ResearchArtifactSha256 || hash != entity.CertificateSha256 ||
            value.Status != StrategyCertificateStatus.ResearchQualified || !value.EligibleForPaperTrading ||
            value.LiveTradingAuthorized || !value.HumanApprovalRequired ||
            nowUtc < entity.IssuedAtUtc || nowUtc >= entity.ExpiresAtUtc)
            throw new InvalidDataException("The strategy certificate is invalid or expired.");
        return value;
    }

    private static int? Minimum(int? left, int? right) => (left, right) switch
    { (null, null) => null, (int value, null) => value, (null, int value) => value, (int a, int b) => Math.Min(a, b) };
    private static string Mode(LiveTradingMode mode) => mode == LiveTradingMode.SemiLive ? "SemiLive" : "DirectLive";
    private static async Task<LiveEntryIntent> ReadInputAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length > 1024 * 1024) throw new ArgumentException("Live-order input exceeds 1 MiB.");
        return await JsonSerializer.DeserializeAsync<LiveEntryIntent>(stream, Json, token) ??
            throw new ArgumentException("Live-order input is empty.");
    }
    private static Command Parse(string[] args)
    {
        string? mode = null; string? certificate = null; string? input = null; string? output = null;
        string? confirm = null; string? authorization = null;
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {args[index]}.");
            switch (args[index])
            {
                case "--mode" when mode is null: mode = args[index + 1]; break;
                case "--certificate-id" when certificate is null: certificate = args[index + 1]; break;
                case "--file" when input is null: input = args[index + 1]; break;
                case "--output" when output is null: output = args[index + 1]; break;
                case "--confirm" when confirm is null: confirm = args[index + 1]; break;
                case "--automation-authorization" when authorization is null: authorization = args[index + 1]; break;
                default: throw new ArgumentException($"Unknown or duplicate option: {args[index]}");
            }
        }
        var parsedMode = mode?.ToLowerInvariant() switch { "semi" => LiveTradingMode.SemiLive,
            "direct" => LiveTradingMode.DirectLive, _ => throw new ArgumentException("--mode must be semi or direct.") };
        if (!Guid.TryParse(certificate, out var certificateId) || certificateId == Guid.Empty)
            throw new ArgumentException("A valid --certificate-id is required.");
        var inputPath = JsonPath(input, "--file", true); var outputPath = JsonPath(output, "--output", false);
        var authorizationPath = string.IsNullOrWhiteSpace(authorization) ? null :
            JsonPath(authorization, "--automation-authorization", true);
        if (File.Exists(outputPath)) throw new IOException("The output file already exists.");
        return new(parsedMode, certificateId, inputPath, outputPath, confirm, authorizationPath);
    }
    private static string JsonPath(string? value, string option, bool mustExist)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{option} is required.");
        var path = Path.GetFullPath(value);
        if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{option} must use .json.");
        if (mustExist && !File.Exists(path)) throw new FileNotFoundException($"The {option} file was not found.", path);
        if (!mustExist && !Directory.Exists(Path.GetDirectoryName(path))) throw new ArgumentException("The output directory does not exist.");
        return path;
    }
    private static async Task WriteNewAtomicallyAsync(string path, string value, CancellationToken token)
    { var temp = $"{path}.tmp-{Guid.NewGuid():N}"; try { await File.WriteAllTextAsync(temp, value, new UTF8Encoding(false), token); File.Move(temp, path, false); } finally { if (File.Exists(temp)) File.Delete(temp); } }
    private static async Task ReplaceAtomicallyAsync(string path, string value, CancellationToken token)
    { var temp = $"{path}.tmp-{Guid.NewGuid():N}"; try { await File.WriteAllTextAsync(temp, value, new UTF8Encoding(false), token); File.Move(temp, path, true); } finally { if (File.Exists(temp)) File.Delete(temp); } }
    private static void Delete(string? path) { if (path is not null && File.Exists(path)) File.Delete(path); }
    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static JsonSerializerOptions Options() { var value = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }; value.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)); return value; }
    private sealed record Command(LiveTradingMode Mode, Guid CertificateId, string Input, string Output,
        string? Confirmation, string? AutomationAuthorization);
    private sealed record PaperEvidence(IReadOnlyList<PaperSessionEvidence> SessionEvidence, string EvidenceSha256);
}

public sealed record PaperSessionEvidence(Guid SessionId, string ArtifactSha256, int FilledTrades, decimal RealizedNetPnl);
public sealed record LiveOrderArtifact(int SchemaVersion, Guid LiveOrderId, DateTime CreatedAtUtc,
    LiveTradingMode Mode, string Status, Guid StrategyCertificateId, string StrategyCertificateSha256,
    IReadOnlyList<PaperSessionEvidence> PaperSessions, string PaperEvidenceSha256,
    BrokerAccountSnapshot AccountSnapshot, BrokerQuote Quote, LiveOrderProposal Proposal,
    BrokerOrderReceipt? BrokerReceipt, bool OperatorApproved, string ArtifactSha256,
    Guid? AutomationDecisionId = null, string? AutomationSha256 = null,
    Guid? StrategyQualificationId = null, Guid? PaperQualificationId = null,
    Guid? ReconciliationId = null);
