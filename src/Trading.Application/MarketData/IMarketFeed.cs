namespace Trading.Application.MarketData;

public enum MarketFeedSource { PaperReplay = 1, ZerodhaSandbox = 2, ZerodhaLive = 3 }
public enum MarketFeedQuoteMode { Ltp = 1, Quote = 2, Full = 3 }

public sealed record MarketFeedSubscription(uint InstrumentToken, string Exchange, string TradingSymbol,
    decimal PriceDivisor = 100m);

public sealed record NormalizedMarketTick(MarketFeedSource Source, MarketFeedQuoteMode Mode,
    uint InstrumentToken, string Exchange, string TradingSymbol, DateTime ReceivedAtUtc,
    DateTime? ExchangeTimestampUtc, decimal LastPrice, long? LastQuantity, decimal? AveragePrice,
    long? Volume, long? BuyQuantity, long? SellQuantity, decimal? Open, decimal? High,
    decimal? Low, decimal? Close, long? OpenInterest, decimal? BestBid, decimal? BestAsk);

public sealed record MarketFeedRequest(MarketFeedSource Source, MarketFeedQuoteMode Mode,
    IReadOnlyList<MarketFeedSubscription> Subscriptions, string? ReplayFile = null);

public interface IMarketFeed
{
    IAsyncEnumerable<NormalizedMarketTick> StreamAsync(MarketFeedRequest request,
        CancellationToken cancellationToken = default);
}
