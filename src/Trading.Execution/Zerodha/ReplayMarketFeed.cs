using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trading.Application.MarketData;

namespace Trading.Execution.Zerodha;

public sealed class ReplayMarketFeed : IMarketFeed
{
    private static readonly JsonSerializerOptions Json = CreateJson();

    public async IAsyncEnumerable<NormalizedMarketTick> StreamAsync(MarketFeedRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Source != MarketFeedSource.PaperReplay)
            throw new ArgumentException("Replay requires the paper-replay source.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.ReplayFile) || !File.Exists(request.ReplayFile))
            throw new FileNotFoundException("The replay file was not found.", request.ReplayFile);
        if (new FileInfo(request.ReplayFile).Length > 16 * 1024 * 1024)
            throw new InvalidDataException("The replay file exceeds the 16 MB safety limit.");
        var subscriptions = ValidateSubscriptions(request.Subscriptions);
        DateTime? previous = null;
        await foreach (var line in File.ReadLinesAsync(request.ReplayFile, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var tick = JsonSerializer.Deserialize<NormalizedMarketTick>(line, Json) ??
                throw new InvalidDataException("A replay line is invalid.");
            if (tick.Source != MarketFeedSource.PaperReplay || tick.Mode != request.Mode ||
                tick.ReceivedAtUtc.Kind != DateTimeKind.Utc ||
                tick.ExchangeTimestampUtc is { Kind: not DateTimeKind.Utc } ||
                tick.LastPrice <= 0 || !subscriptions.TryGetValue(tick.InstrumentToken, out var subscription) ||
                !string.Equals(tick.Exchange, subscription.Exchange, StringComparison.Ordinal) ||
                !string.Equals(tick.TradingSymbol, subscription.TradingSymbol, StringComparison.Ordinal))
                throw new InvalidDataException("A replay tick does not match the requested source, mode, identity, or UTC/price rules.");
            if (previous > tick.ReceivedAtUtc) throw new InvalidDataException("Replay ticks must be chronological.");
            previous = tick.ReceivedAtUtc;
            yield return tick;
        }
    }

    internal static IReadOnlyDictionary<uint, MarketFeedSubscription> ValidateSubscriptions(
        IReadOnlyList<MarketFeedSubscription> subscriptions)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        if (subscriptions.Count is < 1 or > 3000)
            throw new ArgumentException("Provide between 1 and 3000 subscriptions.", nameof(subscriptions));
        var result = new Dictionary<uint, MarketFeedSubscription>();
        foreach (var item in subscriptions)
        {
            if (item.InstrumentToken == 0 || string.IsNullOrWhiteSpace(item.Exchange) ||
                string.IsNullOrWhiteSpace(item.TradingSymbol) || item.PriceDivisor <= 0 ||
                !result.TryAdd(item.InstrumentToken, item))
                throw new ArgumentException("Subscriptions require unique non-zero tokens, identities, and positive divisors.", nameof(subscriptions));
        }
        return result;
    }

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
