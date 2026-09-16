using System.Globalization;
using Trading.Domain.MarketData;
using Trading.MarketData.Import;

namespace Trading.UnitTests;

public sealed class CandleCsvReaderTests
{
    private const string Row = "2026-09-01T09:15:00+05:30,100.00,102.00,99.00,101.00,1000,";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-02T00:00:00Z");

    [Fact]
    public async Task Reads_BOM_UTC_null_interest_and_decimal_using_invariant_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var result = await Read("\uFEFF" + CandleCsvReader.Header + "\r\n" + Row);
            var candle = Assert.Single(result.Candles);
            Assert.Equal(new DateTime(2026, 9, 1, 3, 45, 0, DateTimeKind.Utc), candle.OpenTimeUtc);
            Assert.Equal(101m, candle.Close);
            Assert.Null(candle.OpenInterest);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("2026-09-01T09:15:00")]
    [InlineData("2026-09-01T09:15:01+05:30")]
    [InlineData("2026-09-02T00:00:00Z")]
    public async Task Rejects_ambiguous_unaligned_or_incomplete_timestamps(string timestamp) =>
        await Assert.ThrowsAsync<CandleImportException>(() => Read(CandleCsvReader.Header + "\n" + timestamp + ",100,102,99,101,0,"));

    [Theory]
    [InlineData("timestamp,open,high,low,close,volume")]
    [InlineData("timestamp,open,high,low,close,volume,openInterest")]
    [InlineData("timestamp,open,high,low,close,volume,openInterest\n\n")]
    public async Task Rejects_bad_header_empty_file_and_blank_rows(string csv) =>
        await Assert.ThrowsAsync<CandleImportException>(() => Read(csv));

    [Fact]
    public async Task Rejects_duplicate_instant_expressed_in_another_offset()
    {
        var exception = await Assert.ThrowsAsync<CandleImportException>(() => Read(CandleCsvReader.Header + "\n" + Row + "\n" +
            "2026-09-01T03:45:00Z,100,102,99,101,0,"));
        Assert.Contains("Line 3", exception.Message);
    }

    [Fact]
    public async Task Reports_gaps_without_fabricating_bars()
    {
        var result = await Read(CandleCsvReader.Header + "\n" + Row + "\n" + Row.Replace("09:15", "09:30"));
        Assert.Equal(2, result.Candles.Count);
        Assert.Equal(1, result.GapCount);
    }

    [Theory]
    [InlineData("09:10")]
    [InlineData("09:17")]
    public async Task Rejects_reverse_order_and_overlapping_bars(string time) =>
        await Assert.ThrowsAsync<CandleImportException>(() => Read(CandleCsvReader.Header + "\n" + Row + "\n" + Row.Replace("09:15", time)));

    [Theory]
    [InlineData("100,99,90,100,0,")]
    [InlineData("100.00001,110,90,100,0,")]
    [InlineData("100,110,90,100,-1,")]
    [InlineData("100,110,90,100,0,-1")]
    [InlineData("1e2,110,90,100,0,")]
    [InlineData("100,110,90,100,9223372036854775808,")]
    [InlineData("100,110,90,100,0,1,2")]
    public async Task Reports_invalid_numeric_rows(string fields)
    {
        var exception = await Assert.ThrowsAsync<CandleImportException>(() => Read(CandleCsvReader.Header + "\n2026-09-01T03:45:00Z," + fields));
        Assert.Contains("Line 2", exception.Message);
    }

    [Fact]
    public async Task Enforces_row_limit()
    {
        var start = Now.AddDays(-40);
        var rows = Enumerable.Range(0, 10001).Select(i => $"{start.AddMinutes(i * 5):yyyy-MM-ddTHH:mm:ss}Z,100,110,90,100,0,");
        var exception = await Assert.ThrowsAsync<CandleImportException>(() => Read(CandleCsvReader.Header + "\n" + string.Join('\n', rows)));
        Assert.Contains("10,000", exception.Message);
    }

    private static Task<CandleImportPreview> Read(string csv) => CandleCsvReader.ReadAsync(new StringReader(csv), Guid.NewGuid(), Timeframe.Minute5, Now);
}
