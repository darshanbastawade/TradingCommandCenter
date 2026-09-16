namespace Trading.Backtesting.Costs;

public sealed record TradeCostBreakdown(
    decimal Brokerage,
    decimal SecuritiesTransactionTax,
    decimal ExchangeAndIpftCharges,
    decimal SebiCharges,
    decimal StampDuty,
    decimal Gst,
    decimal Other)
{
    public static TradeCostBreakdown None { get; } = new(0, 0, 0, 0, 0, 0, 0);

    public decimal Total => Brokerage + SecuritiesTransactionTax + ExchangeAndIpftCharges +
        SebiCharges + StampDuty + Gst + Other;
}
