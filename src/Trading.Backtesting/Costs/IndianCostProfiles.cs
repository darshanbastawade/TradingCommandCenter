namespace Trading.Backtesting.Costs;

public static class IndianCostProfiles
{
    private const decimal Gst = 0.18m;
    private const decimal SebiTenPerCrore = 0.000001m;

    /// <summary>Zerodha resident account, NSE equity intraday rates effective March 1, 2026.</summary>
    public static ITradeCostModel ZerodhaNseEquityIntraday2026() => new IndianTradeCostModel(new(
        "zerodha-nse-equity-intraday-2026-03-01",
        FlatBrokeragePerOrder: 20m,
        BrokerageRate: 0.0003m,
        BrokerageIsFlat: false,
        SellSideSttRate: 0.00025m,
        ExchangeAndIpftRate: 0.0000307m,
        SebiRate: SebiTenPerCrore,
        BuySideStampRate: 0.00003m,
        GstRate: Gst));

    /// <summary>Zerodha resident account, NSE equity-options premium rates effective April 1, 2026.</summary>
    public static ITradeCostModel ZerodhaNseEquityOptions2026() => new IndianTradeCostModel(new(
        "zerodha-nse-equity-options-2026-04-01",
        FlatBrokeragePerOrder: 20m,
        BrokerageRate: 0m,
        BrokerageIsFlat: true,
        SellSideSttRate: 0.0015m,
        ExchangeAndIpftRate: 0.0003553m,
        SebiRate: SebiTenPerCrore,
        BuySideStampRate: 0.00003m,
        GstRate: Gst));
}
