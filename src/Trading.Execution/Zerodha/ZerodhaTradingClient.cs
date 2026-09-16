using System.Net.Http.Headers;
using System.Text.Json;
using Trading.Application.Execution;

namespace Trading.Execution.Zerodha;

public sealed class ZerodhaTradingClient(HttpClient httpClient, ZerodhaFeedOptions options) : ILiveBrokerClient
{
    private static readonly Uri ApiRoot = new("https://api.kite.trade/");

    public async Task<BrokerAccountSnapshot> GetAccountSnapshotAsync(CancellationToken cancellationToken = default)
    {
        EnsureCredentials();
        using var margin = await SendAsync(HttpMethod.Get, "user/margins/equity", null, cancellationToken);
        using var positions = await SendAsync(HttpMethod.Get, "portfolio/positions", null, cancellationToken);
        using var orders = await SendAsync(HttpMethod.Get, "orders", null, cancellationToken);
        var marginData = Data(margin).GetProperty("available");
        var available = marginData.TryGetProperty("live_balance", out var liveBalance) ?
            liveBalance.GetDecimal() : marginData.GetProperty("cash").GetDecimal();
        var net = Data(positions).GetProperty("net");
        var items = net.EnumerateArray().Select(item => new BrokerPosition(
            item.GetProperty("instrument_token").GetUInt32(), item.GetProperty("exchange").GetString() ?? "",
            item.GetProperty("tradingsymbol").GetString() ?? "", item.GetProperty("product").GetString() ?? "",
            item.GetProperty("quantity").GetInt32())).ToArray();
        return new(DateTime.UtcNow, available, items, Data(orders).GetArrayLength());
    }

    public async Task<BrokerQuote> GetQuoteAsync(uint instrumentToken, string exchange, string tradingSymbol,
        CancellationToken cancellationToken = default)
    {
        EnsureCredentials();
        var identity = $"{exchange}:{tradingSymbol}";
        using var response = await SendAsync(HttpMethod.Get,
            $"quote?i={Uri.EscapeDataString(identity)}", null, cancellationToken);
        var data = Data(response);
        if (!data.TryGetProperty(identity, out var quote)) throw new InvalidDataException("Zerodha returned no quote for the requested instrument.");
        var depth = quote.GetProperty("depth");
        var buys = depth.GetProperty("buy"); var sells = depth.GetProperty("sell");
        if (buys.GetArrayLength() == 0 || sells.GetArrayLength() == 0)
            throw new InvalidDataException("Zerodha returned no bid/ask depth for the requested instrument.");
        return new(DateTime.UtcNow, instrumentToken, exchange, tradingSymbol,
            quote.GetProperty("last_price").GetDecimal(), buys[0].GetProperty("price").GetDecimal(),
            sells[0].GetProperty("price").GetDecimal());
    }

    public async Task<BrokerOrderReceipt> PlaceLimitBuyAsync(BrokerOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!options.AllowLiveOrders)
            throw new InvalidOperationException("Zerodha live orders are disabled by configuration.");
        EnsureCredentials(); ArgumentNullException.ThrowIfNull(request);
        if (request.RequestId == Guid.Empty || request.Quantity < 1 || request.LimitPrice <= 0 ||
            string.IsNullOrWhiteSpace(request.Exchange) || string.IsNullOrWhiteSpace(request.TradingSymbol) ||
            request.Product != "MIS" || string.IsNullOrWhiteSpace(request.Tag) || request.Tag.Length > 20)
            throw new ArgumentException("The Zerodha limit order is invalid.", nameof(request));
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["tradingsymbol"] = request.TradingSymbol, ["exchange"] = request.Exchange,
            ["transaction_type"] = "BUY", ["order_type"] = "LIMIT",
            ["quantity"] = request.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["product"] = request.Product, ["price"] = request.LimitPrice.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["validity"] = "DAY", ["tag"] = request.Tag
        });
        using var response = await SendAsync(HttpMethod.Post, "orders/regular", form, cancellationToken);
        var orderId = Data(response).GetProperty("order_id").GetString();
        if (string.IsNullOrWhiteSpace(orderId) || orderId.Length > 64)
            throw new InvalidDataException("Zerodha returned an invalid order ID.");
        return new(orderId, DateTime.UtcNow);
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, HttpContent? content,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(ApiRoot, path)) { Content = content };
        request.Headers.Add("X-Kite-Version", "3");
        request.Headers.Authorization = new AuthenticationHeaderValue("token", $"{options.ApiKey}:{options.AccessToken}");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Zerodha request failed with HTTP {(int)response.StatusCode}.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        try
        {
            var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("status", out var status) || status.GetString() != "success")
            {
                document.Dispose(); throw new InvalidDataException("Zerodha returned an unsuccessful response.");
            }
            return document;
        }
        catch (JsonException exception) { throw new InvalidDataException("Zerodha returned invalid JSON.", exception); }
    }

    private static JsonElement Data(JsonDocument document) => document.RootElement.GetProperty("data");
    private void EnsureCredentials()
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey) || string.IsNullOrWhiteSpace(options.AccessToken))
            throw new InvalidOperationException("Configure Zerodha:ApiKey and Zerodha:AccessToken in User Secrets.");
    }
}
