using System.Buffers.Binary;
using Trading.Application.MarketData;
using Trading.Execution.Zerodha;

namespace Trading.IntegrationTests;

public sealed class KiteBinaryTickParserTests
{
    private static readonly MarketFeedSubscription Subscription = new(12345, "NFO", "NIFTY26SEP25000CE", 100m);
    private static readonly IReadOnlyDictionary<uint, MarketFeedSubscription> Subscriptions =
        new Dictionary<uint, MarketFeedSubscription> { [Subscription.InstrumentToken] = Subscription };
    private static readonly DateTime Received = new(2026, 9, 15, 4, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Parses_ltp_packet_and_ignores_heartbeat()
    {
        var packet = new byte[8]; UInt32(packet, 0, 12345); Int32(packet, 4, 123456);
        var tick = Assert.Single(KiteBinaryTickParser.Parse(Frame(packet), MarketFeedSource.ZerodhaSandbox,
            MarketFeedQuoteMode.Ltp, Subscriptions, Received));
        Assert.Equal(1234.56m, tick.LastPrice);
        Assert.Equal("NIFTY26SEP25000CE", tick.TradingSymbol);
        Assert.Empty(KiteBinaryTickParser.Parse([0], MarketFeedSource.ZerodhaSandbox,
            MarketFeedQuoteMode.Full, Subscriptions, Received));
    }

    [Fact]
    public void Parses_full_quote_prices_timestamp_open_interest_and_depth()
    {
        var packet = new byte[184];
        UInt32(packet, 0, 12345); Int32(packet, 4, 10025); Int32(packet, 8, 25); Int32(packet, 12, 10000);
        Int32(packet, 16, 1000); Int32(packet, 20, 600); Int32(packet, 24, 400); Int32(packet, 28, 9900);
        Int32(packet, 32, 10200); Int32(packet, 36, 9800); Int32(packet, 40, 9950); Int32(packet, 48, 5000);
        Int32(packet, 60, 1_789_441_200); Int32(packet, 64, 100); Int32(packet, 68, 10020);
        Int32(packet, 124, 80); Int32(packet, 128, 10030);

        var tick = Assert.Single(KiteBinaryTickParser.Parse(Frame(packet), MarketFeedSource.ZerodhaLive,
            MarketFeedQuoteMode.Full, Subscriptions, Received));
        Assert.Equal(100.25m, tick.LastPrice);
        Assert.Equal(100m, tick.AveragePrice);
        Assert.Equal(99m, tick.Open);
        Assert.Equal(5000, tick.OpenInterest);
        Assert.Equal(100.20m, tick.BestBid);
        Assert.Equal(100.30m, tick.BestAsk);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_789_441_200).UtcDateTime, tick.ExchangeTimestampUtc);
    }

    [Fact]
    public void Rejects_malformed_and_unknown_packets()
    {
        Assert.Throws<InvalidDataException>(() => KiteBinaryTickParser.Parse([0, 1, 0, 8, 0],
            MarketFeedSource.ZerodhaSandbox, MarketFeedQuoteMode.Ltp, Subscriptions, Received));
        var packet = new byte[8]; UInt32(packet, 0, 999); Int32(packet, 4, 10000);
        Assert.Throws<InvalidDataException>(() => KiteBinaryTickParser.Parse(Frame(packet),
            MarketFeedSource.ZerodhaSandbox, MarketFeedQuoteMode.Ltp, Subscriptions, Received));
    }

    private static byte[] Frame(byte[] packet)
    {
        var frame = new byte[packet.Length + 4]; UInt16(frame, 0, 1); UInt16(frame, 2, (ushort)packet.Length);
        packet.CopyTo(frame, 4); return frame;
    }
    private static void UInt16(byte[] target, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(target.AsSpan(offset, 2), value);
    private static void UInt32(byte[] target, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(target.AsSpan(offset, 4), value);
    private static void Int32(byte[] target, int offset, int value) =>
        BinaryPrimitives.WriteInt32BigEndian(target.AsSpan(offset, 4), value);
}
