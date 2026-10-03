using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Trading.DataDownloader;

namespace Trading.IntegrationTests;

public sealed class UpstoxHistoricalDownloaderTests
{
    [Fact]
    public void V3_url_and_month_windows_are_correct_across_year_boundaries()
    {
        var uri = UpstoxHistoricalDownloader.BuildRequestUri("NSE_INDEX|Nifty 50", 1,
            new(2024, 12, 15), new(2024, 12, 31));
        Assert.Equal("https://api.upstox.com/v3/historical-candle/NSE_INDEX%7CNifty%2050/minutes/1/2024-12-31/2024-12-15", uri.AbsoluteUri);
        Assert.Equal(new[]
        {
            (new DateOnly(2024, 12, 15), new DateOnly(2024, 12, 31)),
            (new DateOnly(2025, 1, 1), new DateOnly(2025, 1, 31)),
            (new DateOnly(2025, 2, 1), new DateOnly(2025, 2, 3))
        }, UpstoxHistoricalDownloader.BuildWindows(new(2024, 12, 15), new(2025, 2, 3)));
        Assert.Contains("/minutes/5/", UpstoxHistoricalDownloader.BuildRequestUri("NSE_INDEX|Nifty 50", 5,
            new(2025, 1, 1), new(2025, 1, 31)).AbsolutePath);
    }

    [Fact]
    public void Arguments_reject_unsupported_ranges_current_day_and_unknown_options()
    {
        Assert.True(DownloadOptions.Parse(["--help"]).ShowHelp);
        Assert.Equal(5, DownloadOptions.Parse(Args("--interval", "5")).IntervalMinutes);
        Assert.Throws<ArgumentException>(() => DownloadOptions.Parse(Args("--interval", "3")));
        Assert.Throws<ArgumentException>(() => DownloadOptions.Parse(Args("--from", "2021-12-31")));
        Assert.Throws<ArgumentException>(() => DownloadOptions.Parse(Args("--unknown", "x")));
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
            UpstoxHistoricalDownloader.IndiaTimeZone()).DateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Assert.Throws<ArgumentException>(() => DownloadOptions.Parse(Args("--to", today)));
    }

    [Fact]
    public async Task Download_splits_at_row_limit_writes_interval_manifest_and_resumes_only_verified_files()
    {
        var root = NewDirectory();
        try
        {
            var candles = Enumerable.Range(0, 9501).Select(index => CandleAt(index));
            var handler = new QueueHandler((_, _) => Task.FromResult(JsonResponse(candles)));
            var options = Options(root);
            var summary = await new UpstoxHistoricalDownloader(new HttpClient(handler)).DownloadAsync(options, TextWriter.Null);
            Assert.Equal(9501, summary.TotalRows);
            var dataset = Path.Combine(root, "nse-index-nifty-50", "1m");
            var files = Directory.GetFiles(dataset, "*.csv", SearchOption.AllDirectories).OrderBy(x => x).ToArray();
            Assert.Collection(files, _ => { }, _ => { });
            Assert.All(files, file => Assert.Contains("1m", Path.GetFileName(file)));
            Assert.DoesNotContain(files, file => Path.GetFileName(file).Contains("5m", StringComparison.Ordinal));
            Assert.All(files, file => Assert.True(new FileInfo(file).Length <= UpstoxHistoricalDownloader.MaximumBytesPerFile));
            Assert.Equal(9500, File.ReadLines(files[0]).Skip(1).Count());
            Assert.Single(File.ReadLines(files[1]).Skip(1));
            var manifestPath = Path.Combine(dataset, "manifest.json");
            var manifest = await File.ReadAllTextAsync(manifestPath);
            Assert.Contains("historical-candle-v3", manifest);
            Assert.Contains("\"intervalMinutes\": 1", manifest);
            Assert.Contains("\"status\": \"completed\"", manifest);
            Assert.Contains("\"zeroVolumeRows\": 9501", manifest);
            var resumed = await new UpstoxHistoricalDownloader(new HttpClient(new QueueHandler((_, _) =>
                throw new Xunit.Sdk.XunitException("Completed windows must not be downloaded again."))))
                .DownloadAsync(options with { Resume = true }, TextWriter.Null);
            Assert.Equal(9501, resumed.TotalRows);
            await File.AppendAllTextAsync(files[0], "tamper");
            await Assert.ThrowsAsync<InvalidDataException>(() => new UpstoxHistoricalDownloader(new HttpClient(new QueueHandler((_, _) =>
                throw new Xunit.Sdk.XunitException("Hash validation must happen before HTTP."))))
                .DownloadAsync(options with { Resume = true }, TextWriter.Null));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Retries_rate_limit_and_stops_for_authentication_failure()
    {
        var root = NewDirectory();
        try
        {
            var calls = 0;
            var handler = new QueueHandler((_, _) =>
            {
                calls++;
                if (calls == 1)
                {
                    var limited = new HttpResponseMessage((HttpStatusCode)429);
                    limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                    return Task.FromResult(limited);
                }
                if (calls == 2) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                return Task.FromResult(JsonResponse([CandleAt(0)]));
            });
            await new UpstoxHistoricalDownloader(new HttpClient(handler)).DownloadAsync(Options(root), TextWriter.Null);
            Assert.Equal(3, calls);
            var authRoot = NewDirectory();
            try
            {
                var denied = new QueueHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
                await Assert.ThrowsAsync<UpstoxAuthenticationException>(() => new UpstoxHistoricalDownloader(new HttpClient(denied))
                    .DownloadAsync(Options(authRoot), TextWriter.Null));
            }
            finally { Directory.Delete(authRoot, true); }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Cancellation_preserves_completed_months_and_marks_incomplete_month_failed()
    {
        var root = NewDirectory();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var calls = 0;
            var handler = new QueueHandler((_, _) =>
            {
                calls++;
                if (calls == 1) return Task.FromResult(JsonResponse([CandleAt(0)]));
                cancellation.Cancel();
                return Task.FromCanceled<HttpResponseMessage>(cancellation.Token);
            });
            var options = Options(root) with { To = new DateOnly(2025, 2, 1) };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new UpstoxHistoricalDownloader(new HttpClient(handler))
                .DownloadAsync(options, TextWriter.Null, cancellation.Token));
            var manifestPath = Path.Combine(root, "nse-index-nifty-50", "1m", "manifest.json");
            var manifest = await File.ReadAllTextAsync(manifestPath);
            Assert.Contains("\"status\": \"completed\"", manifest);
            Assert.Contains("\"status\": \"failed\"", manifest);
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(manifestPath)!, "*.csv", SearchOption.AllDirectories));
            var resumeCalls = 0;
            var resumeHandler = new QueueHandler((_, _) =>
            {
                resumeCalls++;
                return Task.FromResult(JsonResponse([CandleAt(new DateTimeOffset(2025, 2, 1, 9, 15, 0, TimeSpan.FromMinutes(330)))]));
            });
            var resumed = await new UpstoxHistoricalDownloader(new HttpClient(resumeHandler)).DownloadAsync(
                Options(root) with { To = new DateOnly(2025, 2, 1), Resume = true }, TextWriter.Null);
            Assert.Equal(1, resumeCalls);
            Assert.Equal(2, resumed.TotalRows);
            Assert.Equal(2, resumed.CompletedWindows);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Partial_manifest_and_atomic_orphan_file_can_be_resumed_safely()
    {
        var root = NewDirectory();
        try
        {
            using (var cancellation = new CancellationTokenSource())
            {
                var handler = new QueueHandler((_, _) =>
                {
                    cancellation.Cancel();
                    return Task.FromCanceled<HttpResponseMessage>(cancellation.Token);
                });
                var options = Options(root) with { To = new DateOnly(2025, 3, 31) };
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new UpstoxHistoricalDownloader(new HttpClient(handler))
                    .DownloadAsync(options, TextWriter.Null, cancellation.Token));
                var partial = await File.ReadAllTextAsync(Path.Combine(root, "nse-index-nifty-50", "1m", "manifest.json"));
                Assert.Single(JsonNode.Parse(partial)!["windows"]!.AsArray());
            }

            var fullOptions = Options(root) with { To = new DateOnly(2025, 3, 31) };
            var initialResumeCalls = 0;
            var original = new QueueHandler((request, _) =>
            {
                initialResumeCalls++;
                var path = request.RequestUri!.AbsolutePath.Split('/');
                var endDate = DateOnly.ParseExact(path[6], "yyyy-MM-dd", CultureInfo.InvariantCulture);
                return Task.FromResult(JsonResponse([CandleAt(new DateTimeOffset(endDate.Year, endDate.Month,
                    2, 9, 15, 0, TimeSpan.FromMinutes(330)))]));
            });
            await new UpstoxHistoricalDownloader(new HttpClient(original)).DownloadAsync(fullOptions with { Resume = true }, TextWriter.Null);
            Assert.Equal(3, initialResumeCalls);
            var manifestPath = Path.Combine(root, "nse-index-nifty-50", "1m", "manifest.json");
            var node = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
            var january = node["windows"]!.AsArray()[0]!;
            january["status"] = "failed";
            january["rowCount"] = 0;
            january["firstTimestamp"] = null;
            january["lastTimestamp"] = null;
            january["files"] = new JsonArray();
            node["totalRows"] = 0;
            node["zeroVolumeRows"] = 0;
            node["actualFrom"] = null;
            node["actualTo"] = null;
            await File.WriteAllTextAsync(manifestPath, node.ToJsonString(new() { WriteIndented = true }));
            var retried = 0;
            var retryHandler = new QueueHandler((_, _) =>
            {
                retried++;
                return Task.FromResult(JsonResponse([CandleAt(0)]));
            });
            var result = await new UpstoxHistoricalDownloader(new HttpClient(retryHandler)).DownloadAsync(
                fullOptions with { Resume = true }, TextWriter.Null);
            Assert.Equal(1, retried);
            Assert.Equal(3, result.CompletedWindows);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Empty_and_duplicate_success_responses_are_not_marked_as_completed_coverage()
    {
        var emptyRoot = NewDirectory();
        var duplicateRoot = NewDirectory();
        try
        {
            var empty = await new UpstoxHistoricalDownloader(new HttpClient(new QueueHandler((_, _) =>
                Task.FromResult(JsonResponse([]))))).DownloadAsync(Options(emptyRoot), TextWriter.Null);
            Assert.Equal(1, empty.EmptyWindows);
            Assert.Equal(0, empty.CompletedWindows);
            var duplicateCandle = CandleAt(0);
            var duplicate = await new UpstoxHistoricalDownloader(new HttpClient(new QueueHandler((_, _) =>
                Task.FromResult(JsonResponse([duplicateCandle, duplicateCandle]))))).DownloadAsync(Options(duplicateRoot), TextWriter.Null);
            Assert.Equal(1, duplicate.FailedWindows);
            Assert.Equal(0, duplicate.CompletedWindows);
            Assert.Empty(Directory.GetFiles(duplicateRoot, "*.csv", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(emptyRoot, true);
            Directory.Delete(duplicateRoot, true);
        }
    }

    private static string[] Args(params string[] changes)
    {
        var args = new List<string>
        {
            "--instrument-key", "NSE_INDEX|Nifty 50", "--interval", "1",
            "--from", "2025-01-01", "--to", "2025-01-31", "--output", Path.GetTempPath()
        };
        for (var i = 0; i < changes.Length; i += 2)
        {
            var position = args.IndexOf(changes[i]);
            if (position >= 0) args[position + 1] = changes[i + 1];
            else { args.Add(changes[i]); args.Add(changes[i + 1]); }
        }
        return [.. args];
    }

    private static DownloadOptions Options(string root) => new("NSE_INDEX|Nifty 50", 1,
        new(2025, 1, 1), new(2025, 1, 31), root, false);

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "trading-downloader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static DownloadedFixture CandleAt(int index)
    {
        var timestamp = new DateTimeOffset(2025, 1, 2, 9, 15, 0, TimeSpan.FromMinutes(330)).AddMinutes(index);
        return new(timestamp, 100, 101, 99, 100, 0, null);
    }

    private static DownloadedFixture CandleAt(DateTimeOffset timestamp) => new(timestamp, 100, 101, 99, 100, 0, null);

    private static HttpResponseMessage JsonResponse(IEnumerable<DownloadedFixture> candles)
    {
        var json = new StringBuilder("{\"status\":\"success\",\"data\":{\"candles\":[");
        var first = true;
        foreach (var candle in candles)
        {
            if (!first) json.Append(',');
            first = false;
            json.Append("[\"").Append(candle.Timestamp.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture))
                .Append("\",").Append(candle.Open).Append(',').Append(candle.High).Append(',').Append(candle.Low)
                .Append(',').Append(candle.Close).Append(',').Append(candle.Volume).Append(",null]");
        }
        json.Append("]}}");
        return new(HttpStatusCode.OK) { Content = new StringContent(json.ToString(), Encoding.UTF8, "application/json") };
    }

    private sealed record DownloadedFixture(DateTimeOffset Timestamp, decimal Open, decimal High, decimal Low,
        decimal Close, long Volume, long? OpenInterest);

    private sealed class QueueHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}