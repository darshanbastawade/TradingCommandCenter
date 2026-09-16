using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Trading.Application.MarketData;

namespace Trading.Execution.Zerodha;

public sealed class ZerodhaMarketFeed(ZerodhaFeedOptions options) : IMarketFeed
{
    public async IAsyncEnumerable<NormalizedMarketTick> StreamAsync(MarketFeedRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Source is not (MarketFeedSource.ZerodhaSandbox or MarketFeedSource.ZerodhaLive))
            throw new ArgumentException("A Zerodha sandbox or live source is required.", nameof(request));
        if (request.Source == MarketFeedSource.ZerodhaLive && !options.AllowLiveFeed)
            throw new InvalidOperationException("Production market data is disabled. Set Zerodha:AllowLiveFeed=true explicitly.");
        if (string.IsNullOrWhiteSpace(options.ApiKey) || string.IsNullOrWhiteSpace(options.AccessToken))
            throw new InvalidOperationException("Configure Zerodha:ApiKey and Zerodha:AccessToken in User Secrets.");
        if (request.Source == MarketFeedSource.ZerodhaSandbox && string.IsNullOrWhiteSpace(options.UserId))
            throw new InvalidOperationException("Configure Zerodha:UserId for sandbox market data.");
        var subscriptions = ReplayMarketFeed.ValidateSubscriptions(request.Subscriptions);
        if (options.ReceiveBufferBytes is < 1024 or > 1_048_576)
            throw new InvalidOperationException("Zerodha:ReceiveBufferBytes must be between 1024 and 1048576.");
        var endpoint = request.Source == MarketFeedSource.ZerodhaLive ? options.LiveWebSocketEndpoint : options.SandboxWebSocketEndpoint;
        var uri = BuildUri(endpoint, request.Source == MarketFeedSource.ZerodhaSandbox);

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(uri, cancellationToken);
        await SendAsync(socket, new { a = "subscribe", v = subscriptions.Keys }, cancellationToken);
        await SendAsync(socket, new { a = "mode", v = new object[] { Mode(request.Mode), subscriptions.Keys } }, cancellationToken);
        var buffer = new byte[options.ReceiveBufferBytes];
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close) yield break;
                    if (result.Count > 0) message.Write(buffer, 0, result.Count);
                    if (message.Length > 4 * 1024 * 1024) throw new InvalidDataException("The Kite WebSocket message exceeds 4 MB.");
                } while (!result.EndOfMessage);
                var payload = message.ToArray();
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    ThrowIfError(payload);
                    continue;
                }
                foreach (var tick in KiteBinaryTickParser.Parse(payload, request.Source, request.Mode,
                             subscriptions, DateTime.UtcNow)) yield return tick;
            }
        }
        finally
        {
            if (socket.State == WebSocketState.Open)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "capture complete", CancellationToken.None);
        }
    }

    private Uri BuildUri(string endpoint, bool sandbox)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var root) || root.Scheme != "wss")
            throw new InvalidOperationException("The configured Zerodha WebSocket endpoint must use wss://.");
        var separator = string.IsNullOrEmpty(root.Query) ? "?" : "&";
        var query = $"api_key={Uri.EscapeDataString(options.ApiKey)}&access_token={Uri.EscapeDataString(options.AccessToken)}";
        if (sandbox) query += $"&user_id={Uri.EscapeDataString(options.UserId)}";
        return new Uri(root + separator + query);
    }

    private static async Task SendAsync(ClientWebSocket socket, object value, CancellationToken token)
    {
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));
        await socket.SendAsync(payload, WebSocketMessageType.Text, true, token);
    }

    private static string Mode(MarketFeedQuoteMode mode) => mode switch
    {
        MarketFeedQuoteMode.Ltp => "ltp", MarketFeedQuoteMode.Quote => "quote",
        MarketFeedQuoteMode.Full => "full", _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static void ThrowIfError(byte[] payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("type", out var type) && type.GetString() == "error")
                throw new InvalidDataException("Zerodha rejected the market-data request.");
        }
        catch (JsonException exception) { throw new InvalidDataException("Zerodha returned invalid text data.", exception); }
    }
}
