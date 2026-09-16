using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Trading.Api;
using Trading.Application.MarketData;
using Trading.Infrastructure.Persistence;

namespace Trading.IntegrationTests;

public sealed class MarketFeedCommandTests
{
    [Fact]
    public async Task Paper_replay_is_bounded_hashed_and_persisted_exactly()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        await using var services = new ServiceCollection()
            .AddDbContext<TradingDbContext>(options => options.UseSqlite(connection))
            .AddScoped<IMarketFeedCaptureStore, MarketFeedCaptureStore>().BuildServiceProvider();
        await using (var scope = services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<TradingDbContext>().Database.EnsureCreatedAsync();
        var prefix = Path.Combine(Path.GetTempPath(), $"m21-{Guid.NewGuid():N}");
        var subscriptions = prefix + "-subscriptions.json"; var replay = prefix + "-replay.ndjson";
        var destination = prefix + "-capture.json";
        try
        {
            await File.WriteAllTextAsync(subscriptions,
                "[{\"instrumentToken\":12345,\"exchange\":\"NFO\",\"tradingSymbol\":\"NIFTY26SEP25000CE\",\"priceDivisor\":100}]");
            var lines = Enumerable.Range(0, 3).Select(index => JsonSerializer.Serialize(new
            {
                source = "paperReplay", mode = "full", instrumentToken = 12345, exchange = "NFO",
                tradingSymbol = "NIFTY26SEP25000CE", receivedAtUtc = $"2026-09-15T04:0{index}:00Z",
                exchangeTimestampUtc = $"2026-09-15T04:0{index}:00Z", lastPrice = 100 + index,
                lastQuantity = 25, averagePrice = 100, volume = 1000, buyQuantity = 500, sellQuantity = 500,
                open = 99, high = 102, low = 98, close = 100, openInterest = 5000, bestBid = 100, bestAsk = 100.05
            }));
            await File.WriteAllLinesAsync(replay, lines);
            var args = new[] { "capture-market-feed", "--source", "paper", "--mode", "full",
                "--subscriptions", subscriptions, "--replay-file", replay, "--max-ticks", "2", "--output", destination };
            var output = new StringWriter(); var error = new StringWriter();
            Assert.Equal(0, await MarketFeedCommands.RunAsync(args, services, new ConfigurationBuilder().Build(), output, error));
            Assert.Equal(string.Empty, error.ToString());
            await using var scope = services.CreateAsyncScope();
            var summary = Assert.Single(await scope.ServiceProvider.GetRequiredService<IMarketFeedCaptureStore>().ListAsync());
            Assert.Equal(2, summary.TickCount);
            var stored = await scope.ServiceProvider.GetRequiredService<IMarketFeedCaptureStore>().FindAsync(summary.Id);
            var file = await File.ReadAllTextAsync(destination);
            Assert.Equal(file, stored!.ArtifactJson);
            using var document = JsonDocument.Parse(file);
            Assert.Equal(2, document.RootElement.GetProperty("ticks").GetArrayLength());
            var persistedHash = document.RootElement.GetProperty("artifactSha256").GetString();
            var unsigned = JsonSerializer.Deserialize<MarketFeedCaptureArtifact>(file, Json())! with { ArtifactSha256 = string.Empty };
            Assert.Equal(persistedHash, Sha256(JsonSerializer.Serialize(unsigned, Json())));
            Assert.Contains("market-feed-captured", output.ToString());
        }
        finally
        {
            foreach (var file in new[] { subscriptions, replay, destination }) if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task Live_capture_fails_closed_before_network_when_not_enabled()
    {
        var prefix = Path.Combine(Path.GetTempPath(), $"m21-{Guid.NewGuid():N}");
        var subscriptions = prefix + "-subscriptions.json"; var destination = prefix + "-capture.json";
        try
        {
            await File.WriteAllTextAsync(subscriptions,
                "[{\"instrumentToken\":12345,\"exchange\":\"NFO\",\"tradingSymbol\":\"TEST\",\"priceDivisor\":100}]");
            await using var services = new ServiceCollection().BuildServiceProvider();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Zerodha:ApiKey"] = "unused", ["Zerodha:AccessToken"] = "unused", ["Zerodha:AllowLiveFeed"] = "false"
            }).Build();
            var error = new StringWriter();
            var result = await MarketFeedCommands.RunAsync(["capture-market-feed", "--source", "live", "--mode", "ltp",
                "--subscriptions", subscriptions, "--max-ticks", "1", "--output", destination], services,
                configuration, TextWriter.Null, error);
            Assert.Equal(2, result);
            Assert.Contains("disabled", error.ToString());
            Assert.False(File.Exists(destination));
        }
        finally
        {
            if (File.Exists(subscriptions)) File.Delete(subscriptions);
            if (File.Exists(destination)) File.Delete(destination);
        }
    }

    private static JsonSerializerOptions Json()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
    private static string Sha256(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
