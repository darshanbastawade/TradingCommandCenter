namespace Trading.Domain.MarketData;

/// <summary>An observed, tradable option quote at a UTC timestamp.</summary>
public sealed class OptionQuote
{
    private OptionQuote() { }

    public OptionQuote(Guid optionContractId, DateTimeOffset timestamp, decimal bid, decimal ask,
        decimal last, long volume, long openInterest)
    {
        if (optionContractId == Guid.Empty) throw new ArgumentException("Contract ID is required.", nameof(optionContractId));
        foreach (var price in new[] { bid, ask, last })
            if (price <= 0 || decimal.Round(price, 4) != price) throw new ArgumentOutOfRangeException(nameof(bid));
        if (ask < bid) throw new ArgumentException("Ask cannot be below bid.");
        if (volume < 0 || openInterest < 0) throw new ArgumentOutOfRangeException(nameof(volume));
        OptionContractId = optionContractId;
        TimestampUtc = timestamp.UtcDateTime;
        Bid = bid;
        Ask = ask;
        Last = last;
        Volume = volume;
        OpenInterest = openInterest;
    }

    public Guid OptionContractId { get; private set; }
    public DateTime TimestampUtc { get; private set; }
    public decimal Bid { get; private set; }
    public decimal Ask { get; private set; }
    public decimal Last { get; private set; }
    public long Volume { get; private set; }
    public long OpenInterest { get; private set; }
    public decimal Spread => Ask - Bid;
    public decimal SpreadBasisPoints => Spread / ((Ask + Bid) / 2m) * 10_000m;
}
