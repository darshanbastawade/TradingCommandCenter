using System.Diagnostics;
using System.Text.Json;
using Trading.Application.Backtesting;
using Trading.Application.Research;

namespace Trading.Backtesting.Research;

public sealed record VectorbtWorkerOptions
{
    public string PythonExecutable { get; init; } = "python";
    public string ScriptPath { get; init; } = "workers/vectorbt/worker.py";
    public string WorkerVersion { get; init; } = "1.1.0";
    public int TimeoutSeconds { get; init; } = 600;
}

public sealed class VectorbtResearchWorker(VectorbtWorkerOptions options) : IResearchBacktestWorker
{
    public const string Id = "vectorbt";
    public string WorkerId => Id;
    public string WorkerVersion => options.WorkerVersion;
    public BacktestEngineRole Role => BacktestEngineRole.ResearchExploration;

    public async Task<ResearchWorkerResult> RunAsync(ResearchWorkerRequest request,
        CancellationToken cancellationToken = default)
    {
        request = ResearchWorkerEvidenceCodec.Seal(request);
        if (options.TimeoutSeconds is < 1 or > 3600) throw new InvalidOperationException("Vectorbt timeout is invalid.");
        var script = ResolveScript(options.ScriptPath);
        if (!File.Exists(script)) throw new FileNotFoundException("The vectorbt worker script was not found.", script);
        var temporary = Path.Combine(Path.GetTempPath(), $"tcc-vectorbt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        var input = Path.Combine(temporary, "request.json");
        var output = Path.Combine(temporary, "result.json");
        try
        {
            await File.WriteAllTextAsync(input, ResearchWorkerEvidenceCodec.Serialize(request), cancellationToken);
            using var process = new Process { StartInfo = StartInfo(script, input, output), EnableRaisingEvents = true };
            if (!process.Start()) throw new InvalidOperationException("The vectorbt worker could not be started.");
            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(true);
                if (cancellationToken.IsCancellationRequested) throw;
                throw new TimeoutException("The vectorbt worker exceeded its configured timeout.");
            }
            var stdout = await standardOutput; var stderr = await standardError;
            if (stdout.Length > 16_384 || stderr.Length > 16_384)
                throw new InvalidDataException("The vectorbt worker emitted excessive console output.");
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Vectorbt worker failed: {Safe(stderr)}");
            if (!File.Exists(output) || new FileInfo(output).Length is < 2 or > 4 * 1024 * 1024)
                throw new InvalidDataException("The vectorbt worker output is missing or too large.");
            var result = JsonSerializer.Deserialize<ResearchWorkerResult>(await File.ReadAllTextAsync(output,
                cancellationToken), JsonOptions()) ?? throw new InvalidDataException("Vectorbt output is empty.");
            if (result.WorkerId != WorkerId || result.WorkerVersion != WorkerVersion || result.Role != Role)
                throw new InvalidDataException("Vectorbt worker identity does not match configuration.");
            return ResearchWorkerEvidenceCodec.Seal(result, request);
        }
        catch (JsonException exception) { throw new InvalidDataException("Vectorbt output JSON is invalid.", exception); }
        finally
        {
            try { Directory.Delete(temporary, true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private ProcessStartInfo StartInfo(string script, string input, string output)
    {
        var value = new ProcessStartInfo(options.PythonExecutable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(script)!
        };
        value.ArgumentList.Add(script); value.ArgumentList.Add("--request"); value.ArgumentList.Add(input);
        value.ArgumentList.Add("--output"); value.ArgumentList.Add(output);
        return value;
    }

    private static string ResolveScript(string configured)
    {
        if (Path.IsPathRooted(configured)) return Path.GetFullPath(configured);
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, configured);
            if (File.Exists(candidate)) return candidate;
            if (File.Exists(Path.Combine(directory.FullName, "TradingCommandCenter.sln"))) return candidate;
        }
        return Path.GetFullPath(configured);
    }

    private static string Safe(string value)
    {
        var line = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length == 0 ? "no diagnostic was returned" : line[..Math.Min(line.Length, 512)];
    }

    private static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false) }
    };
}
