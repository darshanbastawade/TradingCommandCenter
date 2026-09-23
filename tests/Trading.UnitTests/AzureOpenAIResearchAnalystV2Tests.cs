using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Trading.AI;
using Trading.Application.AI;

namespace Trading.UnitTests;

public sealed class AzureOpenAIResearchAnalystV2Tests
{
    [Fact]
    public async Task Sends_stateless_tool_free_structured_request_and_preserves_identity()
    {
        var output = Output();
        var handler = new StubHandler(Response(output));
        using var client = new HttpClient(handler);
        var analyst = new AzureOpenAIResearchAnalystV2(client, Options.Create(Settings()));

        var result = await analyst.AnalyzeAsync(new(CertificateId, "strategy-v1",
            new string('a', 64), new string('b', 64), "{\"evidence\":true}"));

        Assert.Equal("resp_v2", result.ProviderResponseId);
        Assert.Equal(CertificateId, result.Output.CertificateId);
        using var request = JsonDocument.Parse(handler.Body!);
        Assert.False(request.RootElement.GetProperty("store").GetBoolean());
        Assert.False(request.RootElement.TryGetProperty("tools", out _));
        Assert.Equal("json_schema", request.RootElement.GetProperty("text").GetProperty("format")
            .GetProperty("type").GetString());
    }

    [Fact]
    public async Task Rejects_output_with_different_certificate_identity()
    {
        var changed = Output() with { CertificateId = Guid.NewGuid() };
        using var client = new HttpClient(new StubHandler(Response(changed)));
        var analyst = new AzureOpenAIResearchAnalystV2(client, Options.Create(Settings()));
        await Assert.ThrowsAsync<InvalidDataException>(() => analyst.AnalyzeAsync(new(CertificateId,
            "strategy-v1", new string('a', 64), new string('b', 64), "{}")));
    }

    private static readonly Guid CertificateId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static AstraResearchAnalystV2Output Output() => new(2, CertificateId, "strategy-v1",
        "Evidence summary", ["research"], ["cross-engine"], ["robustness"], ["caveat"],
        ["next"], ["warning"], ResearchAnalystV2Recommendation.QualificationReview);
    private static AzureOpenAIOptions Settings() => new()
    {
        Endpoint = "https://fixture.openai.azure.com", Deployment = "astra-deployment", ApiKey = "test-key",
        MaximumInputCharacters = 10_000, MaximumOutputTokens = 1_000, TimeoutSeconds = 10, ReasoningEffort = "low"
    };
    private static string Response(AstraResearchAnalystV2Output output)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        var text = JsonSerializer.Serialize(output, options);
        return JsonSerializer.Serialize(new
        {
            id = "resp_v2", model = "gpt-6-astra", status = "completed",
            output = new[] { new { type = "message", content = new[] { new { type = "output_text", text } } } },
            usage = new { input_tokens = 100, output_tokens = 50 }
        });
    }
    private sealed class StubHandler(string response) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
