using Trading.Risk;

namespace Trading.BacktestTests;

public sealed class PositionSizerTests
{
    [Fact]
    public void Risk_budget_is_rounded_down_to_complete_lots()
    {
        var result = PositionSizer.Calculate(new(749m, 100m, 95m, 25, 100_000m));

        Assert.Equal(5, result.Lots);
        Assert.Equal(125, result.Quantity);
        Assert.Equal(625m, result.TotalRisk);
        Assert.Equal(12_500m, result.CapitalRequired);
        Assert.Equal(124m, result.UnusedRisk);
    }

    [Fact]
    public void Capital_and_maximum_lots_apply_independent_caps()
    {
        var capitalLimited = PositionSizer.Calculate(new(10_000m, 100m, 95m, 25, 10_000m));
        Assert.Equal(100, capitalLimited.Quantity);

        var lotLimited = PositionSizer.Calculate(new(10_000m, 100m, 95m, 25, 100_000m, MaximumLots: 2));
        Assert.Equal(50, lotLimited.Quantity);
    }

    [Fact]
    public void Insufficient_budget_returns_zero_without_rounding_up()
    {
        var result = PositionSizer.Calculate(new(100m, 100m, 95m, 25, 2_000m));
        Assert.False(result.CanTrade);
        Assert.Equal(0, result.Quantity);
        Assert.Equal(100m, result.UnusedRisk);
    }

    [Theory]
    [InlineData(0, 100, 95, 25, 10000)]
    [InlineData(100, 0, 95, 25, 10000)]
    [InlineData(100, 100, 100, 25, 10000)]
    [InlineData(100, 100, 95, 0, 10000)]
    [InlineData(100, 100, 95, 25, 0)]
    public void Invalid_sizing_inputs_are_rejected(decimal risk, decimal entry, decimal stop, int lotSize,
        decimal capital) => Assert.ThrowsAny<ArgumentException>(() =>
            PositionSizer.Calculate(new(risk, entry, stop, lotSize, capital)));
}
