using Trading.Backtesting.Costs;
using Trading.Strategies.Contracts;

namespace Trading.BacktestTests;

public sealed class IndianTradeCostTests
{
    [Fact]
    public void Zerodha_nse_equity_intraday_profile_calculates_each_component()
    {
        var costs = IndianCostProfiles.ZerodhaNseEquityIntraday2026()
            .Calculate(new(100m, 105m, 100, TradeDirection.Long));

        Assert.Equal(6.15m, costs.Brokerage);
        Assert.Equal(3m, costs.SecuritiesTransactionTax);
        Assert.Equal(0.62935m, costs.ExchangeAndIpftCharges);
        Assert.Equal(0.0205m, costs.SebiCharges);
        Assert.Equal(0.3m, costs.StampDuty);
        Assert.Equal(1.223973m, costs.Gst);
        Assert.Equal(11.323823m, costs.Total);
    }

    [Fact]
    public void Zerodha_nse_options_profile_uses_premium_turnover_and_2026_stt()
    {
        var model = IndianCostProfiles.ZerodhaNseEquityOptions2026();
        var costs = model.Calculate(new(100m, 120m, 50, TradeDirection.Long));

        Assert.Equal("zerodha-nse-equity-options-2026-04-01", model.Id);
        Assert.Equal(40m, costs.Brokerage);
        Assert.Equal(9m, costs.SecuritiesTransactionTax);
        Assert.Equal(3.9083m, costs.ExchangeAndIpftCharges);
        Assert.Equal(0.011m, costs.SebiCharges);
        Assert.Equal(0.15m, costs.StampDuty);
        Assert.Equal(7.905474m, costs.Gst);
        Assert.Equal(60.974774m, costs.Total);
    }

    [Fact]
    public void Short_trade_applies_stt_to_entry_sale_and_stamp_to_exit_purchase()
    {
        var costs = IndianCostProfiles.ZerodhaNseEquityIntraday2026()
            .Calculate(new(105m, 100m, 100, TradeDirection.Short));

        Assert.Equal(3m, costs.SecuritiesTransactionTax);
        Assert.Equal(0.3m, costs.StampDuty);
    }

    [Fact]
    public void Stt_half_rupee_rounds_away_from_zero()
    {
        var costs = IndianCostProfiles.ZerodhaNseEquityOptions2026()
            .Calculate(new(1m, 1m, 1_000, TradeDirection.Long));
        Assert.Equal(2m, costs.SecuritiesTransactionTax);
    }

    [Fact]
    public void Invalid_custom_rate_table_is_rejected()
    {
        var rates = new IndianTradeCostRates("invalid", 20, 0, true, -0.1m, 0, 0, 0, 0.18m);
        Assert.Throws<ArgumentOutOfRangeException>(() => new IndianTradeCostModel(rates));
    }
}
