namespace Trading.Execution.Zerodha;

public sealed class ZerodhaFeedOptions
{
    public const string SectionName = "Zerodha";
    public string ApiKey { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string LiveWebSocketEndpoint { get; set; } = "wss://ws.kite.trade";
    public string SandboxWebSocketEndpoint { get; set; } = "wss://ws-sandbox.kite.trade";
    public bool AllowLiveFeed { get; set; }
    public int ReceiveBufferBytes { get; set; } = 65_536;
    public bool AllowLiveOrders { get; set; }
}
