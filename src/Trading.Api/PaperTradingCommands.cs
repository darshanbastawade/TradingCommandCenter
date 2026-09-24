using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trading.Application.Execution;
using Trading.Application.MarketData;
using Trading.Application.Research;
using Trading.Backtesting.Certification;
using Trading.Domain.Execution;
using Trading.Execution.Paper;
using Trading.Risk.Policy;

namespace Trading.Api;

public static class PaperTradingCommands
{
    private const int MaximumInputBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Json = Options();
    private static readonly JsonSerializerOptions CertificateHashJson = new(JsonSerializerDefaults.Web);

    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] == "paper-trade";

    public static async Task<int> RunAsync(string[] args, IServiceProvider services, IConfiguration configuration,
        TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        string? createdFile = null;
        try
        {
            var options = Parse(args);
            var input = await ReadInputAsync(options.Input, cancellationToken);
            if (!input.OperatorApproved) throw new ArgumentException("Paper trading requires operatorApproved=true in the input artifact.");
            var createdAt = DateTime.UtcNow;
            var qualification = options.QualifiedStrategy is null ? null :
                await ReadQualifiedStrategyAsync(options.QualifiedStrategy, createdAt, cancellationToken);
            await using var scope = services.CreateAsyncScope();
            var certificateEntity = await scope.ServiceProvider.GetRequiredService<IStrategyCertificateStore>()
                .FindAsync(options.CertificateId, cancellationToken) ?? throw new ArgumentException("The strategy certificate was not found.");
            var certificate = VerifyCertificate(certificateEntity, createdAt);
            if (qualification is not null && (certificate.StrategyId != qualification.StrategyId ||
                certificate.ResearchRunId != qualification.ResearchRunId))
                throw new InvalidDataException("The M19 paper certificate does not share the M34 strategy and research lineage.");
            var captureEntity = await scope.ServiceProvider.GetRequiredService<IMarketFeedCaptureStore>()
                .FindAsync(options.FeedCaptureId, cancellationToken) ?? throw new ArgumentException("The market-feed capture was not found.");
            var capture = VerifyCapture(captureEntity);
            ValidateAgainstCertificate(input, certificate);

            var sessionId = Guid.NewGuid();
            var settings = configuration.GetSection("RiskPolicy").Get<DeterministicRiskPolicySettings>() ?? new();
            var effective = settings with
            {
                MaximumRiskPerTrade = decimal.Min(settings.MaximumRiskPerTrade, certificate.Constraints.AllowedRisk)
            };
            var orders = input.Orders.Select(item => item with
            {
                MaximumLots = Minimum(item.MaximumLots, certificate.Constraints.MaximumLots)
            }).ToArray();
            var calendar = await ExchangeCalendarLoader.LoadAsync(configuration, cancellationToken);
            var configurationSha256 = Sha256(JsonSerializer.Serialize(new { input, riskPolicy = effective,
                calendarId = calendar.Id, calendarSha256 = calendar.Sha256 }, Json));
            var paperRequest = new PaperTradingRequest(sessionId, createdAt, certificate.StrategyId,
                input.InitialCash, input.SlippageBasisPoints, input.FeeBasisPointsPerSide,
                input.FixedFeePerFill, input.MaximumTickAgeSeconds, input.KillSwitchEngaged,
                orders, capture.Ticks) { QuoteQualityPolicy = input.QuoteQualityPolicy };
            var result = PaperTradingEngine.Run(paperRequest, effective, calendar);
            if (result.EndingCash < 0) throw new InvalidOperationException("Paper trading produced a negative cash balance.");

            string hash; string artifactJson;
            if (qualification is null)
            {
                var unsigned = new PaperTradingSessionArtifact(1, sessionId, createdAt, certificate.CertificateId,
                    certificate.CertificateSha256, capture.CaptureId, capture.ArtifactSha256, effective.PolicyId,
                    certificate.Constraints.CostProfile, configurationSha256, true, result, string.Empty);
                hash = Sha256(JsonSerializer.Serialize(unsigned, Json));
                artifactJson = JsonSerializer.Serialize(unsigned with { ArtifactSha256 = hash }, Json);
            }
            else
            {
                var unsigned = new QualifiedPaperTradingSessionArtifact(2, sessionId, createdAt,
                    certificate.CertificateId, certificate.CertificateSha256, capture.CaptureId,
                    capture.ArtifactSha256, effective.PolicyId, certificate.Constraints.CostProfile,
                    configurationSha256, true, result, qualification.QualificationId,
                    qualification.QualificationSha256, qualification.CertificateId,
                    qualification.CertificateSha256, qualification.QualifiedAtUtc, string.Empty);
                hash = Sha256(JsonSerializer.Serialize(unsigned, Json));
                artifactJson = JsonSerializer.Serialize(unsigned with { ArtifactSha256 = hash }, Json);
            }
            await WriteNewAtomicallyAsync(options.Output, artifactJson, cancellationToken);
            createdFile = options.Output;
            await scope.ServiceProvider.GetRequiredService<IPaperTradingSessionStore>().AddAsync(
                new PaperTradingSession(sessionId, certificate.CertificateId, capture.CaptureId, createdAt,
                    certificate.StrategyId, result.InitialCash, result.EndingCash, result.RealizedNetPnl,
                    result.SubmittedOrders, result.FilledTrades, result.RejectedOrders,
                    configurationSha256, hash, artifactJson, qualification?.QualificationId,
                    qualification?.QualificationSha256, qualification?.CertificateId,
                    qualification?.CertificateSha256, qualification?.QualifiedAtUtc),
                cancellationToken);
            createdFile = null;
            await output.WriteLineAsync(JsonSerializer.Serialize(new { status = "paper-session-completed",
                sessionId, strategyId = certificate.StrategyId, result.SubmittedOrders, result.FilledTrades,
                result.RejectedOrders, result.RealizedNetPnl, result.EndingCash, output = options.Output,
                artifactSha256 = hash }, Json));
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Delete(createdFile); await error.WriteLineAsync("Paper session cancelled; no completed session should be assumed."); return 130;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or IOException or
                                           UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            Delete(createdFile); await error.WriteLineAsync(exception.Message); return 2;
        }
        catch (Exception)
        {
            Delete(createdFile); await error.WriteLineAsync("Paper session failed. No session was persisted."); return 3;
        }
    }

    private static StrategyCertificate VerifyCertificate(Trading.Domain.Research.IssuedStrategyCertificate entity,
        DateTime nowUtc)
    {
        var certificate = JsonSerializer.Deserialize<StrategyCertificate>(entity.CertificateJson, Json) ??
            throw new InvalidDataException("The stored strategy certificate is invalid.");
        var recomputed = Sha256(JsonSerializer.Serialize(certificate with { CertificateSha256 = string.Empty }, CertificateHashJson));
        if (certificate.CertificateId != entity.Id || certificate.ResearchRunId != entity.ResearchRunId ||
            certificate.StrategyId != entity.StrategyId || certificate.CertificateSha256 != entity.CertificateSha256 ||
            certificate.IssuedAtUtc != entity.IssuedAtUtc || certificate.ExpiresAtUtc != entity.ExpiresAtUtc ||
            certificate.ResearchArtifactSha256 != entity.ResearchArtifactSha256 ||
            recomputed != entity.CertificateSha256 || certificate.Status != StrategyCertificateStatus.ResearchQualified ||
            !certificate.EligibleForPaperTrading || certificate.LiveTradingAuthorized ||
            !certificate.HumanApprovalRequired ||
            nowUtc < entity.IssuedAtUtc || nowUtc >= entity.ExpiresAtUtc)
            throw new InvalidDataException("The strategy certificate is invalid, expired, or not paper-trading eligible.");
        return certificate;
    }

    private static MarketFeedCaptureArtifact VerifyCapture(Trading.Domain.MarketData.MarketFeedCapture entity)
    {
        var capture = JsonSerializer.Deserialize<MarketFeedCaptureArtifact>(entity.ArtifactJson, Json) ??
            throw new InvalidDataException("The stored market-feed capture is invalid.");
        var recomputed = Sha256(JsonSerializer.Serialize(capture with { ArtifactSha256 = string.Empty }, Json));
        if (capture.CaptureId != entity.Id || capture.ArtifactSha256 != entity.ArtifactSha256 ||
            recomputed != entity.ArtifactSha256 || capture.Ticks.Count != entity.TickCount || capture.Ticks.Count < 2)
            throw new InvalidDataException("The market-feed capture identity, hash, or tick count is invalid.");
        return capture;
    }

    private static void ValidateAgainstCertificate(PaperTradingInput input, StrategyCertificate certificate)
    {
        if (input.Orders is null) throw new ArgumentException("At least one paper order is required.");
        if (input.InitialCash > certificate.Constraints.MaximumCapital)
            throw new ArgumentException("Initial cash exceeds the certified maximum capital.");
        if (input.SlippageBasisPoints < certificate.Constraints.SlippageBasisPoints)
            throw new ArgumentException("Paper slippage cannot be lower than the certified research assumption.");
        if (input.FeeBasisPointsPerSide == 0 && input.FixedFeePerFill == 0)
            throw new ArgumentException("Paper trading requires a non-zero fee assumption.");
        if (input.Orders.Any(item => item.CapitalPool != TradingCapitalPool.StrategyTesting))
            throw new ArgumentException("M22 paper orders must use the strategyTesting capital pool.");
    }

    private static int? Minimum(int? requested, int? certified) => (requested, certified) switch
    {
        (null, null) => null, (int value, null) => value, (null, int value) => value,
        (int left, int right) => Math.Min(left, right)
    };

    private static async Task<PaperTradingInput> ReadInputAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length > MaximumInputBytes) throw new ArgumentException("Paper input exceeds 1 MiB.");
        return await JsonSerializer.DeserializeAsync<PaperTradingInput>(stream, Json, token) ??
            throw new ArgumentException("Paper input JSON is empty.");
    }

    private static async Task<QualifiedStrategyArtifact> ReadQualifiedStrategyAsync(string path, DateTime nowUtc,
        CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length > MaximumInputBytes) throw new ArgumentException("Qualified strategy exceeds 1 MiB.");
        var value = await JsonSerializer.DeserializeAsync<QualifiedStrategyArtifact>(stream, Json, token) ??
            throw new InvalidDataException("The M34 qualified strategy is empty or invalid.");
        if (!QualifiedStrategyPipeline.Verify(value) || !value.EligibleForPaperQualification ||
            value.SemiLiveAuthorized || value.DirectLiveAuthorized || nowUtc < value.QualifiedAtUtc ||
            nowUtc >= value.ExpiresAtUtc)
            throw new InvalidDataException("The M34 qualified strategy is invalid, expired, or ineligible.");
        return value;
    }

    private static CommandOptions Parse(string[] args)
    {
        string? certificate = null; string? capture = null; string? input = null; string? output = null;
        string? qualifiedStrategy = null;
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {args[index]}.");
            switch (args[index])
            {
                case "--certificate-id" when certificate is null: certificate = args[index + 1]; break;
                case "--feed-capture-id" when capture is null: capture = args[index + 1]; break;
                case "--file" when input is null: input = args[index + 1]; break;
                case "--output" when output is null: output = args[index + 1]; break;
                case "--qualified-strategy" when qualifiedStrategy is null:
                    qualifiedStrategy = args[index + 1]; break;
                default: throw new ArgumentException($"Unknown or duplicate option: {args[index]}");
            }
        }
        if (!Guid.TryParse(certificate, out var certificateId) || certificateId == Guid.Empty)
            throw new ArgumentException("A valid --certificate-id is required.");
        if (!Guid.TryParse(capture, out var captureId) || captureId == Guid.Empty)
            throw new ArgumentException("A valid --feed-capture-id is required.");
        var inputPath = ExistingJson(input, "--file");
        if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("--output is required.");
        var destination = ResolvePath(output);
        if (!string.Equals(Path.GetExtension(destination), ".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("--output must use the .json extension.");
        if (!Directory.Exists(Path.GetDirectoryName(destination))) throw new ArgumentException("The --output directory does not exist.");
        if (File.Exists(destination)) throw new IOException("The output file already exists.");
        return new(certificateId, captureId, inputPath, destination,
            qualifiedStrategy is null ? null : ExistingJson(qualifiedStrategy, "--qualified-strategy"));
    }
    private static string ExistingJson(string? value, string option)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{option} is required.");
        var path = ResolvePath(value);
        if (!File.Exists(path)) throw new FileNotFoundException($"The {option} file was not found.", path);
        if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{option} must use the .json extension.");
        return path;
    }
    private static string ResolvePath(string value)
    {
        if (Path.IsPathFullyQualified(value)) return Path.GetFullPath(value);
        var current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "TradingCommandCenter.sln"))) current = current.Parent;
        return Path.GetFullPath(value, current?.FullName ?? Environment.CurrentDirectory);
    }
    private static async Task WriteNewAtomicallyAsync(string destination, string contents, CancellationToken token)
    {
        var temporary = $"{destination}.tmp-{Guid.NewGuid():N}";
        try { await File.WriteAllTextAsync(temporary, contents, new UTF8Encoding(false), token); File.Move(temporary, destination, false); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void Delete(string? path) { if (path is not null && File.Exists(path)) File.Delete(path); }
    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
    private sealed record CommandOptions(Guid CertificateId, Guid FeedCaptureId, string Input, string Output,
        string? QualifiedStrategy);
}

public sealed record PaperTradingInput(decimal InitialCash, decimal SlippageBasisPoints,
    decimal FeeBasisPointsPerSide, decimal FixedFeePerFill, int MaximumTickAgeSeconds,
    bool KillSwitchEngaged, bool OperatorApproved, IReadOnlyList<PaperTradeIntent> Orders)
{
    public PaperQuoteQualityPolicy QuoteQualityPolicy { get; init; } = PaperQuoteQualityPolicy.RequireBestBidAsk;
}

public sealed record PaperTradingSessionArtifact(int SchemaVersion, Guid SessionId, DateTime CreatedAtUtc,
    Guid StrategyCertificateId, string StrategyCertificateSha256, Guid MarketFeedCaptureId,
    string MarketFeedCaptureSha256, string RiskPolicyId, string CertifiedCostProfile, string ConfigurationSha256,
    bool OperatorApproved, PaperTradingResult Result, string ArtifactSha256);

public sealed record QualifiedPaperTradingSessionArtifact(int SchemaVersion, Guid SessionId, DateTime CreatedAtUtc,
    Guid StrategyCertificateId, string StrategyCertificateSha256, Guid MarketFeedCaptureId,
    string MarketFeedCaptureSha256, string RiskPolicyId, string CertifiedCostProfile, string ConfigurationSha256,
    bool OperatorApproved, PaperTradingResult Result, Guid StrategyQualificationId,
    string StrategyQualificationSha256, Guid QualificationCertificateId,
    string QualificationCertificateSha256, DateTime QualificationStartedAtUtc, string ArtifactSha256);
