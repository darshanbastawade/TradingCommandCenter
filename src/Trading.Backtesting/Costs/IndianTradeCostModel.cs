using Trading.Strategies.Contracts;

namespace Trading.Backtesting.Costs;

public sealed record IndianTradeCostRates(
    string Id,
    decimal FlatBrokeragePerOrder,
    decimal BrokerageRate,
    bool BrokerageIsFlat,
    decimal SellSideSttRate,
    decimal ExchangeAndIpftRate,
    decimal SebiRate,
    decimal BuySideStampRate,
    decimal GstRate);

/// <summary>Component-level calculator for a two-leg Indian exchange trade.</summary>
public sealed class IndianTradeCostModel : ITradeCostModel
{
    private readonly IndianTradeCostRates rates;

    public IndianTradeCostModel(IndianTradeCostRates rates)
    {
        ArgumentNullException.ThrowIfNull(rates);
        if (string.IsNullOrWhiteSpace(rates.Id)) throw new ArgumentException("A rate-table ID is required.", nameof(rates));
        if (rates.FlatBrokeragePerOrder < 0) throw new ArgumentOutOfRangeException(nameof(rates));
        foreach (var rate in new[] { rates.BrokerageRate, rates.SellSideSttRate, rates.ExchangeAndIpftRate,
                     rates.SebiRate, rates.BuySideStampRate, rates.GstRate })
            if (rate is < 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(rates), "Rates must be between zero and one.");
        this.rates = rates;
    }

    public string Id => rates.Id;

    public TradeCostBreakdown Calculate(TradeCostRequest request)
    {
        if (request.EntryPrice <= 0 || request.ExitPrice <= 0 || request.Quantity < 1 ||
            !Enum.IsDefined(request.Direction))
            throw new ArgumentOutOfRangeException(nameof(request), "Prices and quantity must be positive and direction valid.");

        var entryTurnover = request.EntryPrice * request.Quantity;
        var exitTurnover = request.ExitPrice * request.Quantity;
        var buyTurnover = request.Direction == TradeDirection.Long ? entryTurnover : exitTurnover;
        var sellTurnover = request.Direction == TradeDirection.Long ? exitTurnover : entryTurnover;
        var brokerage = Brokerage(entryTurnover) + Brokerage(exitTurnover);
        var stt = decimal.Round(sellTurnover * rates.SellSideSttRate, 0, MidpointRounding.AwayFromZero);
        var exchange = (entryTurnover + exitTurnover) * rates.ExchangeAndIpftRate;
        var sebi = (entryTurnover + exitTurnover) * rates.SebiRate;
        var stamp = buyTurnover * rates.BuySideStampRate;
        var gst = (brokerage + exchange + sebi) * rates.GstRate;
        return new(brokerage, stt, exchange, sebi, stamp, gst, 0);
    }

    private decimal Brokerage(decimal turnover) => rates.BrokerageIsFlat
        ? rates.FlatBrokeragePerOrder
        : decimal.Min(rates.FlatBrokeragePerOrder, turnover * rates.BrokerageRate);
}
