using System.Diagnostics;

namespace Trading.ExternalValidation.Lean;

public sealed class LeanProcessRunner(LeanOptions options) : ILeanProcessRunner
{
    public async Task<LeanProcessResult> RunAsync(LeanMappedInput input,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.CliExecutable) ||
            string.IsNullOrWhiteSpace(options.ProjectDirectory) ||
            !Directory.Exists(options.ProjectDirectory) ||
            !HasExplicitImageVersion(options.Image) ||
            options.TimeoutSeconds is < 1 or > 7200)
            throw new InvalidOperationException(
                "LEAN requires a local CLI executable, project directory, explicit image tag or digest, and valid timeout.");
        if (File.Exists(input.EvidencePath))
            throw new IOException("LEAN evidence output already exists.");
        var start = new ProcessStartInfo(options.CliExecutable)
        {
            WorkingDirectory = Path.GetFullPath(options.ProjectDirectory),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "backtest", Path.GetFullPath(options.ProjectDirectory),
                     "--output", input.OutputDirectory, "--image", options.Image,
                     "--no-update", "--parameter", "tccRunId", input.RunId })
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("LEAN CLI did not start.");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException("LEAN CLI is unavailable at the configured executable.", exception);
        }
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("LEAN backtest exceeded its configured timeout.");
        }
        var stdout = await standardOutput;
        var stderr = await standardError;
        if (stdout.Length > 2_000_000 || stderr.Length > 2_000_000)
            throw new InvalidDataException("LEAN CLI emitted excessive console output.");
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"LEAN backtest failed: {Safe(stderr)}");

        var results = Directory.GetFiles(input.OutputDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .Where(path =>
            {
                var name = Path.GetFileNameWithoutExtension(path);
                return !name.EndsWith("-order-events", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith("-alpha-results", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith("-alpha-insights", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("data-monitor-report-", StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();
        if (results.Length != 1)
            throw new InvalidDataException("Expected exactly one official LEAN backtest result JSON file.");
        return new(results[0], input.EvidencePath);
    }

    private static string Safe(string value)
    {
        var line = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length == 0 ? "no diagnostic was returned" : line[..Math.Min(line.Length, 512)];
    }

    private static bool HasExplicitImageVersion(string? image)
    {
        if (string.IsNullOrWhiteSpace(image) || image.EndsWith(":latest", StringComparison.OrdinalIgnoreCase))
            return false;
        var digest = image.IndexOf("@sha256:", StringComparison.OrdinalIgnoreCase);
        if (digest >= 0) return image[(digest + 8)..].Length == 64 &&
            image[(digest + 8)..].All(Uri.IsHexDigit);
        return image.LastIndexOf(':') > image.LastIndexOf('/') &&
            !image.EndsWith(':') && !image.Contains(' ');
    }
}
