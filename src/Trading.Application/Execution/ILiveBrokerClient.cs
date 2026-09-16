namespace Trading.Application.Execution;

public sealed record BrokerPosition(uint InstrumentToken, string Exchange, string TradingSymbol,
    string Product, int Quantity);
public sealed record BrokerAccountSnapshot(DateTime AsOfUtc, decimal AvailableCash,
    IReadOnlyList<BrokerPosition> Positions, int DayOrderCount);
public sealed record BrokerQuote(DateTime AsOfUtc, uint InstrumentToken, string Exchange,
    string TradingSymbol, decimal LastPrice, decimal BestBid, decimal BestAsk);
public sealed record BrokerOrderRequest(Guid RequestId, string Exchange, string TradingSymbol,
    int Quantity, decimal LimitPrice, string Product, string Tag);
public sealed record BrokerOrderReceipt(string BrokerOrderId, DateTime SubmittedAtUtc);

public interface ILiveBrokerClient
{
    Task<BrokerAccountSnapshot> GetAccountSnapshotAsync(CancellationToken cancellationToken = default);
    Task<BrokerQuote> GetQuoteAsync(uint instrumentToken, string exchange, string tradingSymbol,
        CancellationToken cancellationToken = default);
    Task<BrokerOrderReceipt> PlaceLimitBuyAsync(BrokerOrderRequest request,
        CancellationToken cancellationToken = default);
}
