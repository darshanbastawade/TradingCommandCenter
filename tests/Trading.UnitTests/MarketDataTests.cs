using Trading.Domain.MarketData;

namespace Trading.UnitTests;

public sealed class MarketDataTests
{
    [Fact]
    public void Candle_normalizes_exchange_offset_to_UTC()
    {
        var candle = Create(openTime: new DateTimeOffset(2026, 9, 14, 9, 15, 0, TimeSpan.FromMinutes(330)));
        Assert.Equal(new DateTime(2026, 9, 14, 3, 45, 0, DateTimeKind.Utc), candle.OpenTimeUtc);
        Assert.Equal(DateTimeKind.Utc, candle.OpenTimeUtc.Kind);
    }

    [Theory]
    [InlineData(0, 110, 90, 100)]
    [InlineData(100, 99, 90, 100)]
    [InlineData(100, 110, 101, 105)]
    [InlineData(100, 110, 90, 111)]
    [InlineData(100.00001, 110, 90, 100)]
    public void Candle_rejects_invalid_prices(decimal open, decimal high, decimal low, decimal close) =>
        Assert.ThrowsAny<ArgumentException>(() => new Candle(Guid.NewGuid(), Timeframe.Minute5,
            DateTimeOffset.UtcNow, open, high, low, close, 0));

    [Theory]
    [InlineData(-1L, null)]
    [InlineData(0L, -1L)]
    public void Candle_rejects_negative_volume_or_interest(long volume, long? interest) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Candle(Guid.NewGuid(), Timeframe.Minute5,
            DateTimeOffset.UtcNow, 100, 110, 90, 100, volume, interest));

    [Fact]
    public void Candle_rejects_unknown_timeframe() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Candle(Guid.NewGuid(), (Timeframe)7,
            DateTimeOffset.UtcNow, 100, 110, 90, 100, 0));

    [Fact]
    public void Instrument_normalizes_exchange_and_symbol()
    {
        var instrument = new Instrument(Guid.NewGuid(), " nse ", " nifty ", "Nifty Index", 1, 0.05m);
        Assert.Equal("NSE", instrument.Exchange);
        Assert.Equal("NIFTY", instrument.Symbol);
    }

    [Theory]
    [InlineData(0, 0.05)]
    [InlineData(1, 0)]
    [InlineData(1, 0.00001)]
    public void Instrument_rejects_invalid_lot_or_tick(int lot, decimal tick) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Instrument(Guid.NewGuid(), "NSE", "TEST", "Test", lot, tick));

    private static Candle Create(DateTimeOffset openTime) =>
        new(Guid.NewGuid(), Timeframe.Minute5, openTime, 100, 110, 90, 105, 0);
}
