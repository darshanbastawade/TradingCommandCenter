using System.Net;
using System.Text;
using Trading.Application.Execution;
using Trading.Execution.Zerodha;

namespace Trading.IntegrationTests;

public sealed class ZerodhaTradingClientTests
{
    [Fact]
    public async Task Reads_account_and_quote_and_submits_only_the_expected_limit_buy()
    {
        var handler = new StubHandler();
        var client = new ZerodhaTradingClient(new HttpClient(handler), new()
        { ApiKey = "test-key", AccessToken = "test-token", AllowLiveOrders = true });
        var account = await client.GetAccountSnapshotAsync();
        var quote = await client.GetQuoteAsync(12345, "NFO", "TESTCE");
        var receipt = await client.PlaceLimitBuyAsync(new(Guid.NewGuid(), "NFO", "TESTCE", 50,
            100.2m, "MIS", "tcc123"));

        Assert.Equal(30_000m, account.AvailableCash);
        Assert.Empty(account.Positions);
        Assert.Equal(99.9m, quote.BestBid);
        Assert.Equal(100m, quote.BestAsk);
        Assert.Equal("order-123", receipt.BrokerOrderId);
        Assert.Contains("transaction_type=BUY", handler.LastForm);
        Assert.Contains("order_type=LIMIT", handler.LastForm);
        Assert.Contains("quantity=50", handler.LastForm);
        Assert.Contains("price=100.2", handler.LastForm);
        Assert.DoesNotContain("test-token", handler.LastForm);
    }

    [Fact]
    public async Task Order_adapter_fails_before_HTTP_when_live_orders_are_disabled()
    {
        var handler = new StubHandler();
        var client = new ZerodhaTradingClient(new HttpClient(handler), new()
        { ApiKey = "test-key", AccessToken = "test-token" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.PlaceLimitBuyAsync(
            new(Guid.NewGuid(), "NFO", "TESTCE", 50, 100.2m, "MIS", "tcc123")));
        Assert.Equal(0, handler.RequestCount);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public string LastForm { get; private set; } = string.Empty;
        public int RequestCount { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Assert.Equal("3", Assert.Single(request.Headers.GetValues("X-Kite-Version")));
            Assert.Equal("token", request.Headers.Authorization?.Scheme);
            string json;
            if (request.RequestUri!.AbsolutePath.EndsWith("/user/margins/equity", StringComparison.Ordinal))
                json = "{\"status\":\"success\",\"data\":{\"available\":{\"live_balance\":30000}}}";
            else if (request.RequestUri.AbsolutePath.EndsWith("/portfolio/positions", StringComparison.Ordinal))
                json = "{\"status\":\"success\",\"data\":{\"net\":[]}}";
            else if (request.RequestUri.AbsolutePath.EndsWith("/orders", StringComparison.Ordinal))
                json = "{\"status\":\"success\",\"data\":[]}";
            else if (request.RequestUri.AbsolutePath.EndsWith("/quote", StringComparison.Ordinal))
                json = "{\"status\":\"success\",\"data\":{\"NFO:TESTCE\":{\"last_price\":99.95,\"depth\":{\"buy\":[{\"price\":99.9}],\"sell\":[{\"price\":100}]}}}}";
            else
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                LastForm = await request.Content!.ReadAsStringAsync(cancellationToken);
                json = "{\"status\":\"success\",\"data\":{\"order_id\":\"order-123\"}}";
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
