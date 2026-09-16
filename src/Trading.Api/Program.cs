using Trading.AI;
using Trading.Web;
using Trading.Infrastructure;
using Trading.Api;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Trading.Application.Research;
using System.Text;
using Trading.Application.Execution;
using Trading.Execution.Zerodha;

var command = MarketDataCommands.IsCommand(args) || OosTestCommands.IsCommand(args) ||
    WalkForwardCommands.IsCommand(args) || ResearchIntegrityCommands.IsCommand(args) ||
    DatabaseCommands.IsCommand(args) || OptionDataCommands.IsCommand(args) || OptionsBacktestCommands.IsCommand(args) ||
    RiskPolicyCommands.IsCommand(args) || StrategyCertificateCommands.IsCommand(args) ||
    BacktestAnalystCommands.IsCommand(args) || MarketFeedCommands.IsCommand(args) ||
    PaperTradingCommands.IsCommand(args) || LiveTradingCommands.IsCommand(args);
if (args.Length > 0 && !command && !args[0].StartsWith("--", StringComparison.Ordinal))
{
    Console.Error.WriteLine("Unknown command. See docs/M23.md for available research, feed, paper, live, analysis, certificate, risk, and database commands.");
    Environment.ExitCode = 2;
    return;
}
var builder = WebApplication.CreateBuilder(command ? [] : args);
// Optional machine-local overrides. This file is ignored by Git and may contain developer credentials.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
// Preserve the standard precedence: environment variables and web-host command-line settings win.
builder.Configuration.AddEnvironmentVariables();
if (!command) builder.Configuration.AddCommandLine(args);
builder.Logging.ClearProviders();
if (!command) builder.Logging.AddConsole();
builder.Services.AddTradingAIConfiguration(builder.Configuration);
builder.Services.AddTradingPersistence(builder.Configuration);
var zerodhaOptions = builder.Configuration.GetSection(ZerodhaFeedOptions.SectionName).Get<ZerodhaFeedOptions>() ?? new();
var liveTradingOptions = builder.Configuration.GetSection("LiveTrading").Get<Trading.Execution.Live.LiveTradingSettings>() ?? new();
builder.Services.AddSingleton(zerodhaOptions);
builder.Services.AddHttpClient<ILiveBrokerClient, ZerodhaTradingClient>(client => client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddRazorComponents();
builder.Services.AddHealthChecks().AddCheck<DatabaseReadinessCheck>("database", tags: ["ready"], timeout: TimeSpan.FromSeconds(20));

var app = builder.Build();
if (command)
{
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
    Console.CancelKeyPress += cancel;
    try
    {
        Environment.ExitCode = OosTestCommands.IsCommand(args)
            ? await OosTestCommands.RunAsync(args, app.Services, Console.Out, Console.Error, cancellation.Token)
            : WalkForwardCommands.IsCommand(args)
                ? await WalkForwardCommands.RunAsync(args, app.Services, Console.Out, Console.Error, cancellation.Token)
                : ResearchIntegrityCommands.IsCommand(args)
                    ? await ResearchIntegrityCommands.RunAsync(args, app.Services, Console.Out, Console.Error, cancellation.Token)
                    : DatabaseCommands.IsCommand(args)
                        ? await DatabaseCommands.RunAsync(app.Services, Console.Out, Console.Error, cancellation.Token)
                        : OptionDataCommands.IsCommand(args)
                            ? await OptionDataCommands.RunAsync(args, app.Services, Console.Out, Console.Error, cancellation.Token)
                            : OptionsBacktestCommands.IsCommand(args)
                                ? await OptionsBacktestCommands.RunAsync(args, app.Services, Console.Out, Console.Error, cancellation.Token)
                                : RiskPolicyCommands.IsCommand(args)
                                    ? await RiskPolicyCommands.RunAsync(args, builder.Configuration, Console.Out, Console.Error, cancellation.Token)
                                    : StrategyCertificateCommands.IsCommand(args)
                                        ? await StrategyCertificateCommands.RunAsync(args, app.Services, Console.Out, Console.Error, cancellation.Token)
                                        : BacktestAnalystCommands.IsCommand(args)
                                            ? await BacktestAnalystCommands.RunAsync(args, app.Services, Console.Out, Console.Error, cancellation.Token)
                                            : MarketFeedCommands.IsCommand(args)
                                                ? await MarketFeedCommands.RunAsync(args, app.Services, builder.Configuration, Console.Out, Console.Error, cancellation.Token)
                                                : PaperTradingCommands.IsCommand(args)
                                                    ? await PaperTradingCommands.RunAsync(args, app.Services, builder.Configuration, Console.Out, Console.Error, cancellation.Token)
                                                    : LiveTradingCommands.IsCommand(args)
                                                        ? await LiveTradingCommands.RunAsync(args, app.Services, builder.Configuration, Console.Out, Console.Error, cancellation.Token)
                                    : await MarketDataCommands.RunAsync(args, app.Services, Console.Out, Console.Error, cancellation.Token);
    }
    finally { Console.CancelKeyPress -= cancel; await app.DisposeAsync(); }
    return;
}
app.UseAntiforgery();
app.MapStaticAssets();
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapGet("/api/status", () => new
{
    milestone = "M23",
    strategies = new[]
    {
        "vwap-ema-trend-breakout-v1",
        "opening-range-breakout-v1",
        "ema-pullback-continuation-v1",
        "vwap-reclaim-rejection-v1",
        "adx-trend-continuation-v1"
    },
    research = "certified-persisted-five-strategy-pipeline",
    optionsBacktesting = "observed-quotes-nearest-expiry-one-strike-itm",
    riskPolicy = "deterministic-pre-trade-hard-gates-v1",
    strategyCertificates = "research-qualified-paper-trading-eligibility-v1",
    backtestAnalyst = "azure-openai-astra-structured-analysis-v1",
    marketFeeds = "paper-replay-and-read-only-zerodha-sandbox-live-v1",
    paperTrading = "certificate-and-risk-gated-deterministic-option-buying-v1",
    liveTrading = "risk-gated-semi-and-direct-limit-entry-v1",
    liveTradingKillSwitchEngaged = liveTradingOptions.KillSwitchEngaged,
    directLiveOrdersEnabled = !liveTradingOptions.KillSwitchEngaged && zerodhaOptions.AllowLiveOrders &&
        liveTradingOptions.AllowDirectOrders,
    backtesting = "operational-walk-forward-testing",
    aiIntegration = "azure-openai-responses-explicit-cli-only"
});
app.MapGet("/api/risk-policy", (IConfiguration configuration) =>
    Results.Ok(configuration.GetSection("RiskPolicy").Get<Trading.Risk.Policy.DeterministicRiskPolicySettings>() ?? new()));
app.MapGet("/api/certificates", async (IServiceScopeFactory scopes, IConfiguration configuration,
    CancellationToken token) =>
{
    if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("TradingDatabase")))
        return Results.Ok(new { schemaVersion = 1, certificates = Array.Empty<object>(), message = "Database is not configured." });
    await using var scope = scopes.CreateAsyncScope();
    var certificates = await scope.ServiceProvider.GetRequiredService<IStrategyCertificateStore>()
        .ListAsync(cancellationToken: token);
    var now = DateTime.UtcNow;
    return Results.Ok(new { schemaVersion = 1, certificates = certificates.Select(item => new
    {
        item.Id, item.ResearchRunId, item.StrategyId, item.IssuedAtUtc, item.ExpiresAtUtc, item.Status,
        isCurrentlyValid = item.IssuedAtUtc <= now && now < item.ExpiresAtUtc,
        item.ResearchArtifactSha256, item.CertificateSha256
    }) });
});
app.MapGet("/api/certificates/{id:guid}", async (Guid id, IServiceScopeFactory scopes,
    IConfiguration configuration, CancellationToken token) =>
{
    if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("TradingDatabase")))
        return Results.Problem("Database is not configured.", statusCode: 503);
    await using var scope = scopes.CreateAsyncScope();
    var certificate = await scope.ServiceProvider.GetRequiredService<IStrategyCertificateStore>().FindAsync(id, token);
    return certificate is null ? Results.NotFound() :
        Results.Content(certificate.CertificateJson, "application/json", Encoding.UTF8);
});
app.MapGet("/api/analyses", async (IServiceScopeFactory scopes, IConfiguration configuration,
    CancellationToken token) =>
{
    if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("TradingDatabase")))
        return Results.Ok(new { schemaVersion = 1, analyses = Array.Empty<object>(), message = "Database is not configured." });
    await using var scope = scopes.CreateAsyncScope();
    var analyses = await scope.ServiceProvider.GetRequiredService<IBacktestAnalysisStore>()
        .ListAsync(cancellationToken: token);
    return Results.Ok(new { schemaVersion = 1, analyses });
});
app.MapGet("/api/analyses/{id:guid}", async (Guid id, IServiceScopeFactory scopes,
    IConfiguration configuration, CancellationToken token) =>
{
    if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("TradingDatabase")))
        return Results.Problem("Database is not configured.", statusCode: 503);
    await using var scope = scopes.CreateAsyncScope();
    var analysis = await scope.ServiceProvider.GetRequiredService<IBacktestAnalysisStore>().FindAsync(id, token);
    return analysis is null ? Results.NotFound() :
        Results.Content(analysis.AnalysisJson, "application/json", Encoding.UTF8);
});
app.MapGet("/api/feed-captures", async (IServiceScopeFactory scopes, IConfiguration configuration,
    CancellationToken token) =>
{
    if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("TradingDatabase")))
        return Results.Ok(new { schemaVersion = 1, captures = Array.Empty<object>(), message = "Database is not configured." });
    await using var scope = scopes.CreateAsyncScope();
    var captures = await scope.ServiceProvider.GetRequiredService<Trading.Application.MarketData.IMarketFeedCaptureStore>()
        .ListAsync(cancellationToken: token);
    return Results.Ok(new { schemaVersion = 1, captures });
});
app.MapGet("/api/feed-captures/{id:guid}", async (Guid id, IServiceScopeFactory scopes,
    IConfiguration configuration, CancellationToken token) =>
{
    if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("TradingDatabase")))
        return Results.Problem("Database is not configured.", statusCode: 503);
    await using var scope = scopes.CreateAsyncScope();
    var capture = await scope.ServiceProvider.GetRequiredService<Trading.Application.MarketData.IMarketFeedCaptureStore>()
        .FindAsync(id, token);
    return capture is null ? Results.NotFound() :
        Results.Content(capture.ArtifactJson, "application/json", Encoding.UTF8);
});
app.MapGet("/api/paper-sessions", async (IServiceScopeFactory scopes, IConfiguration configuration,
    CancellationToken token) =>
{
    if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("TradingDatabase")))
        return Results.Ok(new { schemaVersion = 1, sessions = Array.Empty<object>(), message = "Database is not configured." });
    await using var scope = scopes.CreateAsyncScope();
    var sessions = await scope.ServiceProvider.GetRequiredService<Trading.Application.Execution.IPaperTradingSessionStore>()
        .ListAsync(cancellationToken: token);
    return Results.Ok(new { schemaVersion = 1, sessions });
});
app.MapGet("/api/paper-sessions/{id:guid}", async (Guid id, IServiceScopeFactory scopes,
    IConfiguration configuration, CancellationToken token) =>
{
    if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("TradingDatabase")))
        return Results.Problem("Database is not configured.", statusCode: 503);
    await using var scope = scopes.CreateAsyncScope();
    var session = await scope.ServiceProvider.GetRequiredService<Trading.Application.Execution.IPaperTradingSessionStore>()
        .FindAsync(id, token);
    return session is null ? Results.NotFound() :
        Results.Content(session.ArtifactJson, "application/json", Encoding.UTF8);
});
app.MapGet("/api/live-orders", async (IServiceScopeFactory scopes, IConfiguration configuration,
    CancellationToken token) =>
{
    if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("TradingDatabase")))
        return Results.Ok(new { schemaVersion = 1, orders = Array.Empty<object>(), message = "Database is not configured." });
    await using var scope = scopes.CreateAsyncScope();
    var orders = await scope.ServiceProvider.GetRequiredService<ILiveOrderStore>().ListAsync(cancellationToken: token);
    return Results.Ok(new { schemaVersion = 1, orders });
});
app.MapGet("/api/live-orders/{id:guid}", async (Guid id, IServiceScopeFactory scopes,
    IConfiguration configuration, CancellationToken token) =>
{
    if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("TradingDatabase")))
        return Results.Problem("Database is not configured.", statusCode: 503);
    await using var scope = scopes.CreateAsyncScope();
    var order = await scope.ServiceProvider.GetRequiredService<ILiveOrderStore>().FindAsync(id, token);
    return order is null ? Results.NotFound() : Results.Content(order.ArtifactJson, "application/json", Encoding.UTF8);
});
app.MapGet("/api/reports", async (IServiceScopeFactory scopes, IConfiguration configuration, CancellationToken token) =>
{
    if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("TradingDatabase")))
        return Results.Ok(new { schemaVersion = 1, reports = Array.Empty<object>(), message = "Database is not configured." });
    await using var scope = scopes.CreateAsyncScope();
    var runs = await scope.ServiceProvider.GetRequiredService<IResearchRunStore>().ListAsync(cancellationToken: token);
    return Results.Ok(new { schemaVersion = 1, reports = runs.Select(run => new { run.Id, run.CreatedAtUtc,
        run.InstrumentId, timeframe = (int)run.Timeframe, run.FromUtc, run.ToUtc, run.DataSource,
        run.DataVersion, run.CalendarId, run.DatasetSha256, run.ConfigurationSha256,
        run.ArtifactSha256, run.SourceRevision }) });
});
app.MapGet("/api/reports/{id:guid}", async (Guid id, IServiceScopeFactory scopes,
    IConfiguration configuration, CancellationToken token) =>
{
    if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("TradingDatabase")))
        return Results.Problem("Database is not configured.", statusCode: 503);
    await using var scope = scopes.CreateAsyncScope();
    var run = await scope.ServiceProvider.GetRequiredService<IResearchRunStore>().FindAsync(id, token);
    return run is null ? Results.NotFound() : Results.Content(run.ArtifactJson, "application/json", Encoding.UTF8);
});
app.MapRazorComponents<App>();
app.Run();

// Exposes the entry point to in-process integration tests.
public partial class Program;
