using System.Net.Http.Headers;
using System.Text.Json;
using Trading.DataDownloader;

using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
Console.CancelKeyPress += cancel;
try { return await DownloaderCommand.RunAsync(args, Console.Out, Console.Error, cancellation.Token); }
finally { Console.CancelKeyPress -= cancel; }

internal static class DownloaderCommand
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var options = DownloadOptions.Parse(args);
            if (options.ShowHelp)
            {
                await output.WriteLineAsync(DownloadOptions.Usage);
                return 0;
            }
            var token = Environment.GetEnvironmentVariable("UPSTOX_ACCESS_TOKEN");
            if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("UPSTOX_ACCESS_TOKEN environment variable is not configured.");
            using var client = new HttpClient { BaseAddress = new Uri("https://api.upstox.com/") };
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
            var summary = await new UpstoxHistoricalDownloader(client).DownloadAsync(options, output, cancellationToken);
            await output.WriteLineAsync($"Summary: {summary.TotalRows} rows, {summary.ZeroVolumeRows} zero-volume rows, {summary.MissingVolumeRows} missing-volume rows, {summary.CompletedWindows} completed windows, {summary.EmptyWindows} empty windows, {summary.FailedWindows} failed windows.");
            return summary.FailedWindows == 0 && summary.EmptyWindows == 0 ? 0 : 3;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("Download cancelled. Completed monthly files and manifest entries were preserved; rerun with --resume.");
            return 130;
        }
        catch (UpstoxAuthenticationException exception)
        {
            await error.WriteLineAsync(exception.Message);
            return 4;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or HttpRequestException or JsonException)
        {
            await error.WriteLineAsync(exception.Message);
            return 2;
        }
    }
}