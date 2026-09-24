using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Trading.Api;
using Trading.Application.Execution;
using Trading.Domain.Execution;
using Trading.Execution.Live;
using Trading.Execution.Qualification;
using Trading.Execution.Reconciliation;

namespace Trading.IntegrationTests;

public sealed class AuthoritativeReconciliationCommandTests
{
    private static readonly JsonSerializerOptions Json = Options();

    [Theory]
    [InlineData("exact", 0, "")]
    [InlineData("cash", 4, "cash-difference-exceeds-tolerance")]
    [InlineData("position", 4, "unexpected-broker-position")]
    [InlineData("missing-order", 4, "internal-order-missing-at-broker")]
    [InlineData("unexpected-order", 4, "unexpected-broker-order")]
    [InlineData("fill-status", 4, "order-status-mismatch")]
    public async Task Authoritative_command_compares_fake_broker_and_durable_ledger(string scenario,
        int expectedExitCode, string discrepancy)
    {
        var now = DateTime.UtcNow; var paper = Paper(now); var withInternalOrder =
            scenario is "exact" or "cash" or "position" or "missing-order" or "fill-status";
        var fixture = Sources(now, paper.StrategyId, withInternalOrder);
        var brokerOrders = fixture.Broker.Orders;
        var positions = fixture.Broker.Positions;
        var cash = fixture.Broker.AvailableCash;
        if (scenario == "cash") cash -= 100;
        if (scenario == "position") positions = [new(999, "NFO", "UNEXPECTED", "MIS", 25)];
        if (scenario == "missing-order") brokerOrders = [];
        if (scenario == "unexpected-order") brokerOrders = [BrokerOrder("unexpected", "COMPLETE", 25, 101)];
        if (scenario == "fill-status") brokerOrders = [BrokerOrder("broker-1", "PARTIAL", 10, 101)];
        var broker = fixture.Broker with { AvailableCash = cash, Positions = positions, Orders = brokerOrders };
        var result = await RunAsync(paper, fixture.Read, broker);

        Assert.Equal(expectedExitCode, result.ExitCode);
        Assert.NotNull(result.Artifact);
        Assert.Equal(LiveReconciliationSourceMode.AuthoritativeBroker, result.Artifact!.SourceMode);
        if (discrepancy.Length == 0)
        {
            Assert.Equal(LiveReconciliationStatus.Reconciled, result.Artifact.Status);
            Assert.True(LiveReconciliationCommands.IsAuthoritativeForDirect(result.Artifact));
            Assert.NotNull(result.StoredState);
        }
        else
        {
            Assert.Contains(discrepancy, result.Artifact.DiscrepancyCodes);
            Assert.False(result.Artifact.EligibleForControlledAutomation);
            Assert.Null(result.StoredState);
        }
    }

    [Fact]
    public async Task Stale_fake_broker_snapshot_fails_closed()
    {
        var now = DateTime.UtcNow; var paper = Paper(now); var fixture = Sources(now, paper.StrategyId, false);
        var result = await RunAsync(paper, fixture.Read,
            fixture.Broker with { AsOfUtc = now.AddMinutes(-1) });
        Assert.Equal(2, result.ExitCode);
        Assert.Null(result.Artifact);
        Assert.Contains("stale", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Diagnostic_file_reconciliation_cannot_authorize_direct_live()
    {
        var now = DateTime.UtcNow;
        var snapshot = new LiveReconciliationSnapshot(now.AddSeconds(-1), 30_000, 30_000, 1,
            [], [], [], []);
        var provenance = new LiveReconciliationProvenance(LiveReconciliationSourceMode.DiagnosticFile,
            "manual-json", "unverified", new string('a', 64), new string('b', 64), "manual");
        var artifact = LiveReconciliationEngine.Reconcile(Guid.NewGuid(), new string('c', 64),
            "strategy-v1", snapshot, now, provenance: provenance);
        Assert.Equal(LiveReconciliationStatus.Reconciled, artifact.Status);
        Assert.False(artifact.EligibleForControlledAutomation);
        Assert.False(LiveReconciliationCommands.IsAuthoritativeForDirect(artifact));
    }

    private static async Task<Result> RunAsync(PaperQualificationArtifact paper,
        InternalTradingLedgerRead read, BrokerReconciliationState broker)
    {
        var stateStore = new StateStore();
        await using var services = new ServiceCollection()
            .AddSingleton<IInternalTradingLedgerReader>(new LedgerReader(read))
            .AddSingleton<ILiveBrokerReconciliationClient>(new BrokerClient(broker))
            .AddSingleton<IReconciledExecutionStateStore>(stateStore).BuildServiceProvider();
        var prefix = Path.Combine(Path.GetTempPath(), $"m385-{Guid.NewGuid():N}");
        var paperPath = prefix + "-paper.json"; var outputPath = prefix + "-reconciliation.json";
        try
        {
            await File.WriteAllTextAsync(paperPath, PaperQualificationEngine.Serialize(paper));
            var error = new StringWriter();
            var exit = await LiveReconciliationCommands.RunAsync(["reconcile-live",
                "--paper-qualification", paperPath, "--output", outputPath], services,
                new ConfigurationBuilder().Build(), TextWriter.Null, error);
            var artifact = File.Exists(outputPath) ? JsonSerializer.Deserialize<LiveReconciliationArtifact>(
                await File.ReadAllTextAsync(outputPath), Json) : null;
            return new(exit, artifact, stateStore.Value, error.ToString());
        }
        finally
        {
            if (File.Exists(paperPath)) File.Delete(paperPath);
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    private static (InternalTradingLedgerRead Read, BrokerReconciliationState Broker) Sources(
        DateTime now, string strategyId, bool withOrder)
    {
        IReadOnlyList<InternalLedgerOrder> orders = withOrder ?
            [new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "broker-1", 123,
                "NFO", "TESTCE", "Filled", 25, 25, 101)] : [];
        var unsigned = new InternalTradingLedgerArtifact(1, Guid.NewGuid(), now.AddSeconds(-2), "rev-1",
            "zerodha-kite", "USER1", strategyId, 30_000, 500, 0, true, [], orders,
            new string('d', 64), string.Empty);
        var state = InternalTradingLedgerCodec.Seal(unsigned);
        IReadOnlyList<DurableLiveOrderEvidence> liveOrders = withOrder ? [LiveOrder(now, strategyId)] : [];
        var brokerOrders = withOrder ? [BrokerOrder("broker-1", "COMPLETE", 25, 101)] :
            Array.Empty<BrokerReconciliationOrder>();
        return (new(state, liveOrders), new("zerodha-kite", "USER1", now.AddSeconds(-1),
            30_000, [], brokerOrders));
    }

    private static DurableLiveOrderEvidence LiveOrder(DateTime now, string strategyId)
    {
        var id = Guid.NewGuid(); var requestId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var proposal = new LiveOrderProposal(requestId, strategyId, 123, "NFO", "TESTCE", now.AddSeconds(-3),
            100, 100, 100, 90, 120, 25, 1, 250, 2_500, new string('e', 64));
        var unsigned = new LiveOrderArtifact(2, id, now.AddSeconds(-3), LiveTradingMode.DirectLive,
            "Submitted", Guid.NewGuid(), new string('f', 64), [], new string('a', 64),
            new(now.AddSeconds(-3), 30_000, [], 0),
            new(now.AddSeconds(-3), 123, "NFO", "TESTCE", 100, 99, 100), proposal,
            new("broker-1", now.AddSeconds(-3)), true, string.Empty);
        var hash = Sha256(JsonSerializer.Serialize(unsigned, Json));
        var json = JsonSerializer.Serialize(unsigned with { ArtifactSha256 = hash }, Json);
        return new(id, requestId, now.AddSeconds(-3), "DirectLive", "Submitted", strategyId,
            "NFO", "TESTCE", 25, "broker-1", hash, json);
    }

    private static BrokerReconciliationOrder BrokerOrder(string id, string status, int filled, decimal? average) =>
        new(id, 123, "NFO", "TESTCE", status, 25, filled, average);

    private static PaperQualificationArtifact Paper(DateTime now)
    {
        var start = now.AddDays(-10); var cutoff = now.AddSeconds(-1);
        var sessions = Enumerable.Range(0, 10).Select(index => new VerifiedPaperSession(Guid.NewGuid(),
            start.AddDays(index), DateOnly.FromDateTime(start.AddDays(index)), "strategy-v1",
            (index + 1).ToString("x64"), 4, 4, 0, 100)).ToArray();
        return PaperQualificationEngine.EvaluateDurable(Guid.NewGuid(), new string('a', 64),
            Guid.NewGuid(), new string('b', 64), "strategy-v1", start, cutoff, now.AddDays(30),
            sessions.Length, sessions, []);
    }

    private static string Sha256(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false)); return options;
    }
    private sealed record Result(int ExitCode, LiveReconciliationArtifact? Artifact,
        ReconciledExecutionState? StoredState, string Error);
    private sealed class BrokerClient(BrokerReconciliationState value) : ILiveBrokerReconciliationClient
    { public Task<BrokerReconciliationState> GetReconciliationStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(value); }
    private sealed class LedgerReader(InternalTradingLedgerRead value) : IInternalTradingLedgerReader
    { public Task<InternalTradingLedgerRead> ReadAsync(string strategyId, DateTime asOfUtc, CancellationToken cancellationToken = default) => Task.FromResult(value); }
    private sealed class StateStore : IReconciledExecutionStateStore
    { public ReconciledExecutionState? Value { get; private set; } public Task AddAsync(ReconciledExecutionState state, CancellationToken cancellationToken = default) { Value = state; return Task.CompletedTask; } }
}
