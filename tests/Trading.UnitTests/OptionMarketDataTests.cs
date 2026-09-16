using Trading.Domain.MarketData;
using Trading.MarketData.Import;

namespace Trading.UnitTests;

public sealed class OptionMarketDataTests
{
    private static readonly Guid ContractId = Guid.Parse("18181818-1818-1818-1818-181818181818");

    [Fact]
    public void Contract_and_quote_enforce_market_invariants()
    {
        var underlying = Guid.NewGuid();
        var contract = new OptionContract(ContractId, underlying, "nfo", "nifty26sep25000ce",
            new(2026, 9, 24), 25000, OptionRight.Call, 65, .05m);
        var quote = new OptionQuote(contract.Id, DateTimeOffset.Parse("2026-09-15T09:30:00+05:30"),
            100, 101, 100.5m, 10, 20);

        Assert.Equal("NFO", contract.Exchange);
        Assert.Equal(1m / 100.5m * 10_000m, quote.SpreadBasisPoints);
        Assert.Throws<ArgumentException>(() => new OptionQuote(contract.Id, DateTimeOffset.UtcNow, 101, 100, 100, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OptionContract(Guid.NewGuid(), underlying,
            "NFO", "BAD", new(2026, 9, 24), 0, OptionRight.Call, 65, .05m));
    }

    [Fact]
    public async Task Quote_csv_requires_offsets_order_and_valid_spreads()
    {
        var valid = OptionQuoteCsvReader.Header + "\n2026-09-15T09:30:00+05:30,100,101,100.5,10,20";
        var quotes = await OptionQuoteCsvReader.ReadAsync(new StringReader(valid), ContractId,
            DateTimeOffset.Parse("2026-09-16T00:00:00Z"));
        Assert.Single(quotes);
        await Assert.ThrowsAsync<CandleImportException>(() => OptionQuoteCsvReader.ReadAsync(new StringReader(
            OptionQuoteCsvReader.Header + "\n2026-09-15T09:30:00,100,101,100.5,10,20"), ContractId,
            DateTimeOffset.Parse("2026-09-16T00:00:00Z")));
        await Assert.ThrowsAsync<CandleImportException>(() => OptionQuoteCsvReader.ReadAsync(new StringReader(
            OptionQuoteCsvReader.Header + "\n2026-09-15T09:30:00Z,102,101,100.5,10,20"), ContractId,
            DateTimeOffset.Parse("2026-09-16T00:00:00Z")));
    }
}
