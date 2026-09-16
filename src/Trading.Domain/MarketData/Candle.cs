namespace Trading.Domain.MarketData;

/// <summary>A completed OHLCV bar, keyed by instrument, timeframe and UTC bar-open time.</summary>
public sealed class Candle
{
    private Candle() { }

    public Candle(Guid instrumentId, Timeframe timeframe, DateTimeOffset openTime,
        decimal open, decimal high, decimal low, decimal close, long volume, long? openInterest = null)
    {
        if (instrumentId == Guid.Empty) throw new ArgumentException("Instrument ID is required.", nameof(instrumentId));
        if (!Enum.IsDefined(timeframe)) throw new ArgumentOutOfRangeException(nameof(timeframe));
        foreach (var price in new[] { open, high, low, close })
            if (price <= 0 || price > 99999999999999.9999m || decimal.Round(price, 4) != price)
                throw new ArgumentOutOfRangeException(nameof(open), "Prices must be positive decimal(18,4) values.");
        if (high < low || high < open || high < close || low > open || low > close)
            throw new ArgumentException("OHLC prices must lie within the low/high range.");
        if (volume < 0) throw new ArgumentOutOfRangeException(nameof(volume));
        if (openInterest < 0) throw new ArgumentOutOfRangeException(nameof(openInterest));
        InstrumentId = instrumentId;
        Timeframe = timeframe;
        OpenTimeUtc = openTime.UtcDateTime;
        Open = open; High = high; Low = low; Close = close;
        Volume = volume; OpenInterest = openInterest;
    }

    public Guid InstrumentId { get; private set; }
    public Timeframe Timeframe { get; private set; }
    public DateTime OpenTimeUtc { get; private set; }
    public decimal Open { get; private set; }
    public decimal High { get; private set; }
    public decimal Low { get; private set; }
    public decimal Close { get; private set; }
    public long Volume { get; private set; }
    public long? OpenInterest { get; private set; }
}
