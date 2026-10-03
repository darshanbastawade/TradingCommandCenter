using Trading.Domain.MarketData;
using Trading.Strategies.PriceOnly;

namespace Trading.StrategyValidationTests;

public sealed class PriceOnlyStrategyTests
{
    private static readonly Guid Id = Guid.Parse("7787840d-44f9-4b47-b8ee-6c0ac40c7a01");
    private static readonly TimeZoneInfo India = TimeZoneInfo.CreateCustomTimeZone("Price India", TimeSpan.FromMinutes(330), "India", "India");
    private static Candle[] Bars(long volume = 0) => Enumerable.Range(0, 150).Select(i =>
    {
        var time = new DateTimeOffset(2025, 3, 26, 9, 15, 0, TimeSpan.FromMinutes(330)).AddDays(i / 75).AddMinutes(i % 75 * 5);
        var price = 1000m + i * 2;
        return new Candle(Id, Timeframe.Minute5, time, price, price + 3, price - 1, price + 2, volume);
    }).ToArray();

    [Fact]
    public void Zero_volume_orb_emits_after_warmup_with_no_vwap_evidence()
    {
        var strategy = new PriceOnlyStrategy(PriceOnlySetup.OpeningRangeBreakout);
        var bars = Bars();
        Assert.Empty(strategy.Evaluate(bars.Take(49).ToArray(), India));
        var signals = strategy.Evaluate(bars, India);
        Assert.NotEmpty(signals);
        Assert.All(signals, s => { Assert.Null(s.Evidence.SessionVwap); Assert.Null(s.Evidence.PreviousAverageVolume); });
    }

    [Theory]
    [InlineData(PriceOnlySetup.OpeningRangeBreakout)]
    [InlineData(PriceOnlySetup.EmaPullback)]
    [InlineData(PriceOnlySetup.AdxContinuation)]
    public void Volume_does_not_change_entries_and_future_bars_do_not_rewrite_them(PriceOnlySetup setup)
    {
        var strategy = new PriceOnlyStrategy(setup);
        var zero = strategy.Evaluate(Bars(), India);
        var funded = strategy.Evaluate(Bars(500), India);
        Assert.Equal(zero.Select(s => (s.OpenTimeUtc, s.Direction, s.EntryPrice, s.StopPrice, s.TargetPrice)),
            funded.Select(s => (s.OpenTimeUtc, s.Direction, s.EntryPrice, s.StopPrice, s.TargetPrice)));
        var prefix = Bars().Take(90).ToArray();
        Assert.Equal(strategy.Evaluate(prefix, India), zero.Where(s => s.OpenTimeUtc <= prefix[^1].OpenTimeUtc));
        Assert.Contains("price-only", strategy.Id);
    }

    [Fact]
    public void One_minute_input_is_rejected()
    {
        var b = Bars()[0];
        var minute = new Candle(Id, Timeframe.Minute1, new DateTimeOffset(b.OpenTimeUtc), b.Open, b.High, b.Low, b.Close, 0);
        Assert.Throws<ArgumentException>(() => new PriceOnlyStrategy(PriceOnlySetup.OpeningRangeBreakout).Evaluate([minute], India));
    }
}
