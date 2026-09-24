using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Trading.Api;
using Trading.Application.Execution;
using Trading.Application.Research;
using Trading.Backtesting.Certification;
using Trading.Backtesting.Ranking;
using Trading.Domain.Execution;
using Trading.Domain.Research;
using Trading.Execution.Automation;
using Trading.Execution.Live;

namespace Trading.IntegrationTests;

public sealed class LiveTradingCommandTests
{
    private const string Strategy = "qualified-strategy-v1";
    private static readonly JsonSerializerOptions Json = Options();

    [Fact]
    public async Task Direct_requires_M37_before_any_broker_resolution()
    {
        using var files = await FixtureAsync(); var broker = new FakeBroker();
        await using var services = new ServiceCollection().AddSingleton<ILiveBrokerClient>(broker).BuildServiceProvider();
        var result = await RunAsync(files, services, false);
        Assert.Equal(2, result.Exit); Assert.Contains("--automation-authorization", result.Error);
        Assert.Equal(0, broker.TotalCalls);
    }

    [Theory]
    [InlineData(ControlledAutomationMode.DirectLive, false)]
    [InlineData(ControlledAutomationMode.Observe, true)]
    [InlineData(ControlledAutomationMode.SemiLive, true)]
    public async Task Blocked_observe_and_proposal_artifacts_cannot_reach_broker(
        ControlledAutomationMode mode, bool enabled)
    {
        using var files = await FixtureAsync(mode, enabled); var broker = new FakeBroker();
        await using var services = new ServiceCollection().AddSingleton<ILiveBrokerClient>(broker).BuildServiceProvider();
        var result = await RunAsync(files, services);
        Assert.True(result.Exit == 2, result.Error); Assert.Equal(0, broker.TotalCalls);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("action")]
    [InlineData("reference")]
    [InlineData("hash")]
    public async Task Expired_mismatched_action_and_invalid_hash_fail_before_broker(string fault)
    {
        using var files = await FixtureAsync(); var value = ReadAuthorization(files.Authorization);
        value = fault switch
        {
            "expired" => ControlledAutomationEngine.Seal(value with { EvaluatedAtUtc = DateTime.UtcNow.AddMinutes(-2),
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1), AutomationSha256 = string.Empty }),
            "action" => ControlledAutomationEngine.Seal(value with { ActionId = Guid.NewGuid(), AutomationSha256 = string.Empty }),
            "reference" => ControlledAutomationEngine.Seal(value with { ActionReference = "unrelated-action",
                AutomationSha256 = string.Empty }),
            _ => value with { AutomationSha256 = new string('f', 64) }
        };
        await File.WriteAllTextAsync(files.Authorization, ControlledAutomationEngine.Serialize(value));
        var broker = new FakeBroker();
        await using var services = new ServiceCollection().AddSingleton<ILiveBrokerClient>(broker).BuildServiceProvider();
        var result = await RunAsync(files, services);
        Assert.Equal(2, result.Exit); Assert.Equal(0, broker.TotalCalls);
    }

    [Fact]
    public async Task Strategy_mismatch_fails_before_broker_resolution()
    {
        using var files = await FixtureAsync(strategy: "other"); var certificate = Certificate(); var broker = new FakeBroker();
        await using var services = new ServiceCollection().AddSingleton<IStrategyCertificateStore>(new CertificateStore(certificate.Entity))
            .AddSingleton<ILiveBrokerClient>(broker).BuildServiceProvider();
        var result = await RunAsync(files, services);
        Assert.Equal(2, result.Exit); Assert.Contains("strategy does not match", result.Error); Assert.Equal(0, broker.TotalCalls);
    }

    [Fact]
    public async Task Valid_chain_submits_once_and_records_v2_linkage()
    {
        using var files = await FixtureAsync(); var certificate = Certificate(); var paper = Paper(certificate.Value);
        var broker = new FakeBroker(); var orders = new OrderStore(); var authorizations = new AuthorizationStore();
        await using var services = new ServiceCollection()
            .AddSingleton<IStrategyCertificateStore>(new CertificateStore(certificate.Entity))
            .AddSingleton<IPaperTradingSessionStore>(new PaperStore(paper)).AddSingleton<ILiveOrderStore>(orders)
            .AddSingleton<IControlledAutomationAuthorizationStore>(authorizations)
            .AddSingleton<ILiveBrokerClient>(broker).BuildServiceProvider();
        var result = await RunAsync(files, services);
        Assert.Equal(0, result.Exit); Assert.Equal(1, broker.PlaceCalls);
        Assert.Equal(1, authorizations.ConsumeCalls);
        var artifact = JsonSerializer.Deserialize<LiveOrderArtifact>(Assert.Single(orders.Records).ArtifactJson, Json)!;
        var authorization = ReadAuthorization(files.Authorization);
        Assert.Equal(2, artifact.SchemaVersion); Assert.Equal(authorization.AutomationDecisionId, artifact.AutomationDecisionId);
        Assert.Equal(authorization.AutomationSha256, artifact.AutomationSha256);
        Assert.Equal(authorization.StrategyQualificationId, artifact.StrategyQualificationId);
        Assert.Equal(authorization.PaperQualificationId, artifact.PaperQualificationId);
        Assert.Equal(authorization.ReconciliationId, artifact.ReconciliationId);
    }

    [Fact]
    public async Task Semi_live_is_submission_free_and_legacy_artifact_is_readable()
    {
        using var files = await FixtureAsync(); var certificate = Certificate(); var broker = new FakeBroker();
        var authorizations = new AuthorizationStore();
        await using var services = new ServiceCollection().AddSingleton<IStrategyCertificateStore>(new CertificateStore(certificate.Entity))
            .AddSingleton<IPaperTradingSessionStore>(new PaperStore(Paper(certificate.Value)))
            .AddSingleton<ILiveOrderStore>(new OrderStore())
            .AddSingleton<IControlledAutomationAuthorizationStore>(authorizations)
            .AddSingleton<ILiveBrokerClient>(broker).BuildServiceProvider();
        var result = await RunAsync(files, services, false, "semi");
        Assert.Equal(0, result.Exit); Assert.Equal(0, broker.PlaceCalls); Assert.Equal(0, authorizations.ConsumeCalls);
        var legacy = JsonSerializer.Deserialize<LiveOrderArtifact>("{\"schemaVersion\":1}", Json);
        Assert.NotNull(legacy); Assert.Null(legacy.AutomationDecisionId);
    }

    [Fact]
    public async Task Uncertain_broker_outcome_keeps_authorization_consumed()
    {
        using var files = await FixtureAsync(); var certificate = Certificate();
        var authorizations = new AuthorizationStore(); var broker = new FakeBroker(true);
        await using var services = new ServiceCollection()
            .AddSingleton<IStrategyCertificateStore>(new CertificateStore(certificate.Entity))
            .AddSingleton<IPaperTradingSessionStore>(new PaperStore(Paper(certificate.Value)))
            .AddSingleton<ILiveOrderStore>(new OrderStore())
            .AddSingleton<IControlledAutomationAuthorizationStore>(authorizations)
            .AddSingleton<ILiveBrokerClient>(broker).BuildServiceProvider();
        var result = await RunAsync(files, services);
        Assert.Equal(4, result.Exit); Assert.Equal(1, broker.PlaceCalls);
        Assert.True(await authorizations.IsConsumedAsync(ReadAuthorization(files.Authorization).AutomationDecisionId));
    }

    private static async Task<(int Exit, string Error)> RunAsync(Files files, IServiceProvider services,
        bool authorization = true, string mode = "direct")
    {
        var args = new List<string> { "live-order", "--mode", mode, "--certificate-id", Certificate().Value.CertificateId.ToString(),
            "--file", files.Input, "--output", files.Output };
        if (mode == "direct") args.AddRange(["--confirm", "PLACE-LIVE-ORDER"]);
        if (authorization) args.AddRange(["--automation-authorization", files.Authorization]);
        var error = new StringWriter(); var exit = await LiveTradingCommands.RunAsync(args.ToArray(), services,
            Configuration(), TextWriter.Null, error); return (exit, error.ToString());
    }

    private static async Task<Files> FixtureAsync(ControlledAutomationMode mode = ControlledAutomationMode.DirectLive,
        bool enabled = true, string strategy = Strategy)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"m381-{Guid.NewGuid():N}"); Directory.CreateDirectory(directory);
        var now = DateTime.UtcNow; var request = Guid.NewGuid(); const string reference = "fixture-action";
        var intent = new LiveEntryIntent(request, Guid.NewGuid(), 12345, "NFO", "TESTCE", now, now.AddMinutes(1),
            100.2m, 90, 110, 25, 2, true) { AutomationActionReference = reference };
        var settings = new ControlledAutomationSettings { Enabled = enabled, AllowSemiLive = true,
            AllowDirectLive = true, KillSwitchEngaged = false };
        var artifact = ControlledAutomationEngine.Evaluate(Guid.NewGuid(), new string('a', 64), Guid.NewGuid(),
            new string('b', 64), Guid.NewGuid(), new string('c', 64), strategy, now.AddSeconds(-1),
            new(request, mode, reference, 1, true, "ALLOW-CONTROLLED-AUTOMATION"),
            new(now.AddSeconds(-1), DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now,
                TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"))), true, 0, 0, 0, 0, 0,
                new string('d', 64), string.Empty), now, settings);
        var input = Path.Combine(directory, "input.json"); var auth = Path.Combine(directory, "auth.json");
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(intent, Json));
        await File.WriteAllTextAsync(auth, ControlledAutomationEngine.Serialize(artifact));
        return new(directory, input, auth, Path.Combine(directory, "output.json"));
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    { ["LiveTrading:KillSwitchEngaged"]="false", ["LiveTrading:AllowDirectOrders"]="true",
      ["LiveTrading:MinimumPaperSessions"]="1", ["LiveTrading:MinimumPaperFilledTrades"]="1",
      ["LiveTrading:MaximumBrokerDataAgeSeconds"]="30", ["Zerodha:AllowLiveOrders"]="true",
      ["ExchangeCalendar:DefaultSessionOpen"]="00:00:00", ["ExchangeCalendar:DefaultSessionClose"]="23:59:59",
      ["RiskPolicy:EntryWindowStart"]="00:00:00", ["RiskPolicy:LastEntryTime"]="23:58:00",
      ["RiskPolicy:MandatoryExitTime"]="23:59:00" }).Build();

    private static (StrategyCertificate Value, IssuedStrategyCertificate Entity) Certificate()
    {
        var now = DateTime.UtcNow; var score = new StrategyScore(1, Strategy, 90, true, [], 15,15,15,15,15,10,5);
        var unsigned = new StrategyCertificate(1, Guid.Parse("38100000-0000-0000-0000-000000000001"), "v1",
            StrategyCertificateStatus.ResearchQualified, Guid.NewGuid(), Strategy, now.AddMinutes(-1), now.AddDays(1),
            Guid.NewGuid(), 5, now.AddYears(-1), now.AddMonths(-1), new string('d',64), new string('e',64),
            new string('f',64), "fixture", new(750,100000,2,5,"cost"), score, true, false, true, string.Empty);
        var value = unsigned with { CertificateSha256 = Sha256(JsonSerializer.Serialize(unsigned, new JsonSerializerOptions(JsonSerializerDefaults.Web))) };
        return (value, new(value.CertificateId, value.ResearchRunId, value.StrategyId, value.IssuedAtUtc, value.ExpiresAtUtc,
            value.Status.ToString(), value.ResearchArtifactSha256, value.CertificateSha256, JsonSerializer.Serialize(value, Json)));
    }

    private static PaperTradingSession Paper(StrategyCertificate certificate)
    {
        var id=Guid.NewGuid(); var now=DateTime.UtcNow.AddSeconds(-1);
        var result=new Trading.Execution.Paper.PaperTradingResult(1,id,now,Strategy,30000,30100,100,0,100,1,1,0,[]);
        var unsigned=new PaperTradingSessionArtifact(1,id,now,certificate.CertificateId,certificate.CertificateSha256,
            Guid.NewGuid(),new string('1',64),"risk","cost",new string('2',64),true,result,string.Empty);
        var hash=Sha256(JsonSerializer.Serialize(unsigned,Json)); var artifact=unsigned with { ArtifactSha256=hash };
        return new(id,certificate.CertificateId,artifact.MarketFeedCaptureId,now,Strategy,30000,30100,100,1,1,0,
            artifact.ConfigurationSha256,hash,JsonSerializer.Serialize(artifact,Json));
    }

    private static ControlledAutomationArtifact ReadAuthorization(string path) => JsonSerializer.Deserialize<ControlledAutomationArtifact>(File.ReadAllText(path),Json)!;
    private static string Sha256(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static JsonSerializerOptions Options(){var x=new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true};x.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));return x;}
    private sealed record Files(string Directory,string Input,string Authorization,string Output):IDisposable{public void Dispose(){if(System.IO.Directory.Exists(Directory))System.IO.Directory.Delete(Directory,true);}}
    private sealed class CertificateStore(IssuedStrategyCertificate value):IStrategyCertificateStore{public Task<IssuedStrategyCertificate?> FindAsync(Guid id,CancellationToken t=default)=>Task.FromResult(id==value.Id?value:null);public Task AddRangeAsync(IReadOnlyList<IssuedStrategyCertificate>x,CancellationToken t=default)=>Task.CompletedTask;public Task<IReadOnlyList<StrategyCertificateSummary>> ListAsync(int l=100,CancellationToken t=default)=>Task.FromResult<IReadOnlyList<StrategyCertificateSummary>>([]);}
    private sealed class PaperStore(PaperTradingSession value):IPaperTradingSessionStore{public Task AddAsync(PaperTradingSession x,CancellationToken t=default)=>Task.CompletedTask;public Task<PaperTradingSession?> FindAsync(Guid id,CancellationToken t=default)=>Task.FromResult<PaperTradingSession?>(id==value.Id?value:null);public Task<IReadOnlyList<PaperTradingSessionSummary>> ListAsync(int l=100,CancellationToken t=default)=>Task.FromResult<IReadOnlyList<PaperTradingSessionSummary>>([]);public Task<IReadOnlyList<PaperTradingSession>> ListForCertificateAsync(Guid id,CancellationToken t=default)=>Task.FromResult<IReadOnlyList<PaperTradingSession>>(id==value.StrategyCertificateId?[value]:[]);}
    private sealed class OrderStore:ILiveOrderStore{public List<LiveOrderRecord> Records{get;}=[];public Task AddAsync(LiveOrderRecord x,CancellationToken t=default){Records.Add(x);return Task.CompletedTask;}public Task UpdateAsync(LiveOrderRecord x,CancellationToken t=default)=>Task.CompletedTask;public Task<LiveOrderRecord?> FindAsync(Guid id,CancellationToken t=default)=>Task.FromResult(Records.SingleOrDefault(x=>x.Id==id));public Task<IReadOnlyList<LiveOrderSummary>> ListAsync(int l=100,CancellationToken t=default)=>Task.FromResult<IReadOnlyList<LiveOrderSummary>>([]);}
    private sealed class AuthorizationStore : IControlledAutomationAuthorizationStore
    {
        private readonly HashSet<Guid> consumed = [];
        public int ConsumeCalls { get; private set; }
        public Task<bool> TryConsumeAsync(ControlledAutomationReservation value, DateTime consumedAtUtc,
            CancellationToken cancellationToken = default)
        { ConsumeCalls++; return Task.FromResult(consumed.Add(value.AutomationDecisionId)); }
        public Task<bool> IsConsumedAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(consumed.Contains(id));
    }
    private sealed class FakeBroker(bool throwOnPlace = false):ILiveBrokerClient{public int AccountCalls,QuoteCalls,PlaceCalls;public int TotalCalls=>AccountCalls+QuoteCalls+PlaceCalls;public Task<BrokerAccountSnapshot> GetAccountSnapshotAsync(CancellationToken t=default){AccountCalls++;return Task.FromResult(new BrokerAccountSnapshot(DateTime.UtcNow,30000,[],0));}public Task<BrokerQuote> GetQuoteAsync(uint i,string e,string s,CancellationToken t=default){QuoteCalls++;return Task.FromResult(new BrokerQuote(DateTime.UtcNow,i,e,s,100,99.9m,100));}public Task<BrokerOrderReceipt> PlaceLimitBuyAsync(BrokerOrderRequest r,CancellationToken t=default){PlaceCalls++;return throwOnPlace?Task.FromException<BrokerOrderReceipt>(new HttpRequestException("uncertain")):Task.FromResult(new BrokerOrderReceipt("broker",DateTime.UtcNow));}}
}
