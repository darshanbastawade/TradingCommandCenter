using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trading.Application.MarketData;
using Trading.Domain.MarketData;
using Trading.Execution.Zerodha;

namespace Trading.Api;

public static class MarketFeedCommands
{
    private static readonly JsonSerializerOptions Json = CreateJson();

    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] == "capture-market-feed";

    public static async Task<int> RunAsync(string[] args, IServiceProvider services, IConfiguration configuration,
        TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        string? createdFile = null;
        try
        {
            var settings = Parse(args);
            var subscriptions = await ReadSubscriptionsAsync(settings.SubscriptionsFile, cancellationToken);
            IMarketFeed feed = settings.Source == MarketFeedSource.PaperReplay
                ? new ReplayMarketFeed()
                : new ZerodhaMarketFeed(configuration.GetSection(ZerodhaFeedOptions.SectionName).Get<ZerodhaFeedOptions>() ?? new());
            var request = new MarketFeedRequest(settings.Source, settings.Mode, subscriptions, settings.ReplayFile);
            var ticks = new List<NormalizedMarketTick>(settings.MaximumTicks);
            await foreach (var tick in feed.StreamAsync(request, cancellationToken))
            {
                ticks.Add(tick);
                if (ticks.Count >= settings.MaximumTicks) break;
            }
            if (ticks.Count == 0) throw new InvalidDataException("The feed produced no market ticks.");

            var id = Guid.NewGuid(); var createdAt = DateTime.UtcNow;
            var unsigned = new MarketFeedCaptureArtifact(1, id, createdAt, settings.Source, settings.Mode,
                subscriptions, ticks, string.Empty);
            var hash = Sha256(JsonSerializer.Serialize(unsigned, Json));
            var artifact = unsigned with { ArtifactSha256 = hash };
            var artifactJson = JsonSerializer.Serialize(artifact, Json);
            await WriteNewAtomicallyAsync(settings.Output, artifactJson, cancellationToken);
            createdFile = settings.Output;
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IMarketFeedCaptureStore>().AddAsync(new MarketFeedCapture(
                id, createdAt, Source(settings.Source), Mode(settings.Mode), ticks.Count, ticks[0].ReceivedAtUtc,
                ticks[^1].ReceivedAtUtc, hash, artifactJson), cancellationToken);
            createdFile = null;
            await output.WriteLineAsync(JsonSerializer.Serialize(new { status = "market-feed-captured", captureId = id,
                source = Source(settings.Source), mode = Mode(settings.Mode), tickCount = ticks.Count,
                output = settings.Output, artifactSha256 = hash }, Json));
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Delete(createdFile); await error.WriteLineAsync("Market-feed capture cancelled; no completed capture should be assumed."); return 130;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or IOException or
                                           UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            Delete(createdFile); await error.WriteLineAsync(exception.Message); return 2;
        }
        catch (Exception)
        {
            Delete(createdFile); await error.WriteLineAsync("Market-feed capture failed. No capture was persisted."); return 3;
        }
    }

    private static CaptureSettings Parse(string[] args)
    {
        string? source = null; string? mode = null; string? subscriptions = null; string? replay = null;
        string? maximum = null; string? output = null;
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {args[index]}.");
            switch (args[index])
            {
                case "--source" when source is null: source = args[index + 1]; break;
                case "--mode" when mode is null: mode = args[index + 1]; break;
                case "--subscriptions" when subscriptions is null: subscriptions = args[index + 1]; break;
                case "--replay-file" when replay is null: replay = args[index + 1]; break;
                case "--max-ticks" when maximum is null: maximum = args[index + 1]; break;
                case "--output" when output is null: output = args[index + 1]; break;
                default: throw new ArgumentException($"Unknown or duplicate option: {args[index]}");
            }
        }
        var parsedSource = source?.ToLowerInvariant() switch
        {
            "paper" or "paper-replay" => MarketFeedSource.PaperReplay,
            "sandbox" or "zerodha-sandbox" => MarketFeedSource.ZerodhaSandbox,
            "live" or "zerodha-live" => MarketFeedSource.ZerodhaLive,
            _ => throw new ArgumentException("--source must be paper, sandbox, or live.")
        };
        var parsedMode = mode?.ToLowerInvariant() switch
        {
            "ltp" => MarketFeedQuoteMode.Ltp, "quote" => MarketFeedQuoteMode.Quote,
            "full" => MarketFeedQuoteMode.Full, _ => throw new ArgumentException("--mode must be ltp, quote, or full.")
        };
        if (!int.TryParse(maximum, out var maxTicks) || maxTicks is < 1 or > 10_000)
            throw new ArgumentException("--max-ticks must be between 1 and 10000.");
        var subscriptionsFile = ExistingJson(subscriptions, "--subscriptions");
        string? replayFile = null;
        if (parsedSource == MarketFeedSource.PaperReplay) replayFile = ExistingFile(replay, "--replay-file");
        else if (replay is not null) throw new ArgumentException("--replay-file is valid only with --source paper.");
        if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("--output is required.");
        var destination = ResolvePath(output);
        if (!string.Equals(Path.GetExtension(destination), ".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("--output must use the .json extension.");
        if (!Directory.Exists(Path.GetDirectoryName(destination))) throw new ArgumentException("The --output directory does not exist.");
        if (File.Exists(destination)) throw new IOException("The output file already exists.");
        return new(parsedSource, parsedMode, subscriptionsFile, replayFile, maxTicks, destination);
    }

    private static async Task<IReadOnlyList<MarketFeedSubscription>> ReadSubscriptionsAsync(string path, CancellationToken token)
    {
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("The subscriptions file exceeds 1 MB.");
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<MarketFeedSubscription>>(stream, Json, token) ??
            throw new InvalidDataException("The subscriptions file is invalid.");
    }

    private static string ExistingJson(string? value, string option)
    {
        var path = ExistingFile(value, option);
        if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{option} must use the .json extension.");
        return path;
    }
    private static string ExistingFile(string? value, string option)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{option} is required.");
        var path = ResolvePath(value);
        if (!File.Exists(path)) throw new FileNotFoundException($"The {option} file was not found.", path);
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
    private static string Source(MarketFeedSource source) => JsonNamingPolicy.CamelCase.ConvertName(source.ToString());
    private static string Mode(MarketFeedQuoteMode mode) => JsonNamingPolicy.CamelCase.ConvertName(mode.ToString());
    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
    private sealed record CaptureSettings(MarketFeedSource Source, MarketFeedQuoteMode Mode,
        string SubscriptionsFile, string? ReplayFile, int MaximumTicks, string Output);
}

public sealed record MarketFeedCaptureArtifact(int SchemaVersion, Guid CaptureId, DateTime CreatedAtUtc,
    MarketFeedSource Source, MarketFeedQuoteMode Mode, IReadOnlyList<MarketFeedSubscription> Subscriptions,
    IReadOnlyList<NormalizedMarketTick> Ticks, string ArtifactSha256);
