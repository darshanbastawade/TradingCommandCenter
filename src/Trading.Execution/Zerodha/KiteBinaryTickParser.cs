using System.Buffers.Binary;
using Trading.Application.MarketData;

namespace Trading.Execution.Zerodha;

public static class KiteBinaryTickParser
{
    public static IReadOnlyList<NormalizedMarketTick> Parse(ReadOnlySpan<byte> frame,
        MarketFeedSource source, MarketFeedQuoteMode requestedMode,
        IReadOnlyDictionary<uint, MarketFeedSubscription> subscriptions, DateTime receivedAtUtc)
    {
        if (source is not (MarketFeedSource.ZerodhaLive or MarketFeedSource.ZerodhaSandbox))
            throw new ArgumentException("A Zerodha source is required.", nameof(source));
        if (receivedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("The receive timestamp must be UTC.", nameof(receivedAtUtc));
        ArgumentNullException.ThrowIfNull(subscriptions);
        if (frame.Length == 1) return [];
        if (frame.Length < 2) throw new InvalidDataException("The Kite binary frame is truncated.");

        var packetCount = ReadUInt16(frame, 0);
        if (packetCount is 0 or > 3000) throw new InvalidDataException("The Kite packet count is invalid.");
        var ticks = new List<NormalizedMarketTick>(packetCount);
        var offset = 2;
        for (var index = 0; index < packetCount; index++)
        {
            if (offset + 2 > frame.Length) throw new InvalidDataException("The Kite packet length is truncated.");
            var length = ReadUInt16(frame, offset); offset += 2;
            if (offset + length > frame.Length) throw new InvalidDataException("The Kite packet payload is truncated.");
            ticks.Add(ParsePacket(frame.Slice(offset, length), source, requestedMode, subscriptions, receivedAtUtc));
            offset += length;
        }
        if (offset != frame.Length) throw new InvalidDataException("The Kite binary frame contains trailing data.");
        return ticks;
    }

    private static NormalizedMarketTick ParsePacket(ReadOnlySpan<byte> packet, MarketFeedSource source,
        MarketFeedQuoteMode requestedMode, IReadOnlyDictionary<uint, MarketFeedSubscription> subscriptions,
        DateTime receivedAtUtc)
    {
        if (packet.Length is not (8 or 28 or 32 or 44 or 184))
            throw new InvalidDataException($"Unsupported Kite packet length: {packet.Length}.");
        var token = ReadUInt32(packet, 0);
        if (!subscriptions.TryGetValue(token, out var subscription))
            throw new InvalidDataException($"Kite returned an unsubscribed instrument token: {token}.");
        if (subscription.PriceDivisor <= 0) throw new InvalidDataException("The price divisor must be positive.");
        var actualMode = packet.Length == 8 ? MarketFeedQuoteMode.Ltp :
            packet.Length is 28 or 44 ? MarketFeedQuoteMode.Quote : MarketFeedQuoteMode.Full;
        if (actualMode > requestedMode)
            throw new InvalidDataException("Kite returned a packet richer than the requested quote mode.");
        var lastPrice = Price(packet, 4, subscription.PriceDivisor);
        if (lastPrice <= 0) throw new InvalidDataException("Kite returned a non-positive last price.");

        if (packet.Length == 8)
            return new(source, actualMode, token, subscription.Exchange, subscription.TradingSymbol, receivedAtUtc,
                null, lastPrice, null, null, null, null, null, null, null, null, null, null, null, null);

        if (packet.Length is 28 or 32)
            return new(source, actualMode, token, subscription.Exchange, subscription.TradingSymbol, receivedAtUtc,
                packet.Length == 32 ? Timestamp(packet, 28) : null, lastPrice, null, null, null, null, null,
                Price(packet, 16, subscription.PriceDivisor), Price(packet, 8, subscription.PriceDivisor),
                Price(packet, 12, subscription.PriceDivisor), Price(packet, 20, subscription.PriceDivisor), null, null, null);

        return new(source, actualMode, token, subscription.Exchange, subscription.TradingSymbol, receivedAtUtc,
            packet.Length == 184 ? Timestamp(packet, 60) : null, lastPrice, ReadInt32(packet, 8),
            Price(packet, 12, subscription.PriceDivisor), ReadInt32(packet, 16), ReadInt32(packet, 20),
            ReadInt32(packet, 24), Price(packet, 28, subscription.PriceDivisor),
            Price(packet, 32, subscription.PriceDivisor), Price(packet, 36, subscription.PriceDivisor),
            Price(packet, 40, subscription.PriceDivisor), packet.Length == 184 ? ReadInt32(packet, 48) : null,
            packet.Length == 184 ? Price(packet, 68, subscription.PriceDivisor) : null,
            packet.Length == 184 ? Price(packet, 128, subscription.PriceDivisor) : null);
    }

    private static DateTime? Timestamp(ReadOnlySpan<byte> value, int offset)
    {
        var seconds = ReadInt32(value, offset);
        if (seconds <= 0) return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime; }
        catch (ArgumentOutOfRangeException exception) { throw new InvalidDataException("Kite returned an invalid timestamp.", exception); }
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> value, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(value.Slice(offset, 2));
    private static uint ReadUInt32(ReadOnlySpan<byte> value, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(value.Slice(offset, 4));
    private static int ReadInt32(ReadOnlySpan<byte> value, int offset) =>
        BinaryPrimitives.ReadInt32BigEndian(value.Slice(offset, 4));
    private static decimal Price(ReadOnlySpan<byte> value, int offset, decimal divisor) =>
        ReadInt32(value, offset) / divisor;
}
