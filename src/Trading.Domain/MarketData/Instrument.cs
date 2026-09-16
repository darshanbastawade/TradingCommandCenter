namespace Trading.Domain.MarketData;

public sealed class Instrument
{
    private Instrument() { }

    public Instrument(Guid id, string exchange, string symbol, string name, int lotSize, decimal tickSize)
    {
        if (id == Guid.Empty) throw new ArgumentException("Instrument ID is required.", nameof(id));
        if (lotSize <= 0) throw new ArgumentOutOfRangeException(nameof(lotSize));
        if (tickSize <= 0 || tickSize > 99999999999999.9999m || decimal.Round(tickSize, 4) != tickSize)
            throw new ArgumentOutOfRangeException(nameof(tickSize));
        Id = id;
        Exchange = Required(exchange, 16, nameof(exchange)).ToUpperInvariant();
        Symbol = Required(symbol, 64, nameof(symbol)).ToUpperInvariant();
        Name = Required(name, 128, nameof(name));
        LotSize = lotSize;
        TickSize = tickSize;
    }

    public Guid Id { get; private set; }
    public string Exchange { get; private set; } = string.Empty;
    public string Symbol { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public int LotSize { get; private set; }
    public decimal TickSize { get; private set; }

    private static string Required(string value, int maximum, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maximum)
            throw new ArgumentException($"A value of up to {maximum} characters is required.", parameter);
        return value.Trim();
    }
}
