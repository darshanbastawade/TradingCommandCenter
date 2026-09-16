using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Trading.AI;
using Trading.Application.AI;

namespace Trading.UnitTests;

public sealed class AzureOpenAIBacktestAnalystTests
{
    [Fact]
    public async Task Sends_stateless_structured_Astra_request_and_validates_output()
    {
        var output = new BacktestAnalystOutput(1, RunId, "Evidence is mixed.",
            [new("strategy-v1", "Qualified by supplied ranking.", ["positive OOS"], ["small sample"],
                ["parameter sensitivity"], ["trend concentration"], ["cost sensitivity"],
                AnalystRecommendation.PaperTest)], ["strategies overlap"], ["paper trade"], ["not live authority"]);
        var handler = new StubHandler(Response(output));
        using var client = new HttpClient(handler);
        var analyst = new AzureOpenAIBacktestAnalyst(client, Options.Create(Settings()));

        var result = await analyst.AnalyzeAsync(new(RunId, new string('a', 64), "{\"schemaVersion\":1}", ["strategy-v1"]));

        Assert.Equal("resp_test", result.ProviderResponseId);
        Assert.Equal(123, result.InputTokens);
        Assert.Equal(45, result.OutputTokens);
        Assert.Equal(AnalystRecommendation.PaperTest, Assert.Single(result.Output.StrategyAnalyses).Recommendation);
        Assert.Equal("https://fixture.openai.azure.com/openai/v1/responses", handler.Uri!.ToString());
        Assert.Equal("test-key", handler.ApiKey);
        using var request = JsonDocument.Parse(handler.Body!);
        Assert.Equal("astra-deployment", request.RootElement.GetProperty("model").GetString());
        Assert.False(request.RootElement.GetProperty("store").GetBoolean());
        Assert.Equal("json_schema", request.RootElement.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
        Assert.False(request.RootElement.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task Rejects_structured_output_that_omits_expected_strategy()
    {
        var output = new BacktestAnalystOutput(1, RunId, "Summary", [], [], [], []);
        using var client = new HttpClient(new StubHandler(Response(output)));
        var analyst = new AzureOpenAIBacktestAnalyst(client, Options.Create(Settings()));
        await Assert.ThrowsAsync<InvalidDataException>(() => analyst.AnalyzeAsync(
            new(RunId, new string('a', 64), "{}", ["strategy-v1"])));
    }

    [Fact]
    public async Task Missing_credentials_fail_before_network_request()
    {
        var handler = new StubHandler(Response(new(1, RunId, "x", [], [], [], [])));
        using var client = new HttpClient(handler);
        var analyst = new AzureOpenAIBacktestAnalyst(client, Options.Create(new AzureOpenAIOptions()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => analyst.AnalyzeAsync(
            new(RunId, new string('a', 64), "{}", ["strategy-v1"])));
        Assert.Null(handler.Uri);
    }

    private static readonly Guid RunId = Guid.Parse("20202020-2020-2020-2020-202020202020");
    private static AzureOpenAIOptions Settings() => new()
    {
        Endpoint = "https://fixture.openai.azure.com", Deployment = "astra-deployment", ApiKey = "test-key",
        MaximumInputCharacters = 10_000, MaximumOutputTokens = 1_000, TimeoutSeconds = 10, ReasoningEffort = "low"
    };

    private static string Response(BacktestAnalystOutput output)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        var outputText = JsonSerializer.Serialize(output, options);
        return JsonSerializer.Serialize(new
        {
            id = "resp_test", model = "gpt-6-astra", status = "completed",
            output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = outputText } } } },
            usage = new { input_tokens = 123, output_tokens = 45 }
        });
    }

    private sealed class StubHandler(string response) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? ApiKey { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            ApiKey = Assert.Single(request.Headers.GetValues("api-key"));
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
