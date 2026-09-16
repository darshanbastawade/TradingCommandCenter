namespace Trading.Domain.MarketData;

public enum OptionRight { Call = 1, Put = 2 }

public sealed class OptionContract
{
    private OptionContract() { }

    public OptionContract(Guid id, Guid underlyingInstrumentId, string exchange, string symbol,
        DateOnly expiryDate, decimal strike, OptionRight right, int lotSize, decimal tickSize)
    {
        if (id == Guid.Empty || underlyingInstrumentId == Guid.Empty)
            throw new ArgumentException("Contract and underlying IDs are required.");
        if (!Enum.IsDefined(right)) throw new ArgumentOutOfRangeException(nameof(right));
        if (expiryDate == default) throw new ArgumentOutOfRangeException(nameof(expiryDate));
        if (strike <= 0 || decimal.Round(strike, 4) != strike) throw new ArgumentOutOfRangeException(nameof(strike));
        if (lotSize <= 0) throw new ArgumentOutOfRangeException(nameof(lotSize));
        if (tickSize <= 0 || decimal.Round(tickSize, 4) != tickSize)
            throw new ArgumentOutOfRangeException(nameof(tickSize));
        Id = id;
        UnderlyingInstrumentId = underlyingInstrumentId;
        Exchange = Required(exchange, 16, nameof(exchange)).ToUpperInvariant();
        Symbol = Required(symbol, 96, nameof(symbol)).ToUpperInvariant();
        ExpiryDate = expiryDate;
        Strike = strike;
        Right = right;
        LotSize = lotSize;
        TickSize = tickSize;
    }

    public Guid Id { get; private set; }
    public Guid UnderlyingInstrumentId { get; private set; }
    public string Exchange { get; private set; } = string.Empty;
    public string Symbol { get; private set; } = string.Empty;
    public DateOnly ExpiryDate { get; private set; }
    public decimal Strike { get; private set; }
    public OptionRight Right { get; private set; }
    public int LotSize { get; private set; }
    public decimal TickSize { get; private set; }

    private static string Required(string value, int maximum, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maximum)
            throw new ArgumentException($"A value of up to {maximum} characters is required.", parameter);
        return value.Trim();
    }
}
