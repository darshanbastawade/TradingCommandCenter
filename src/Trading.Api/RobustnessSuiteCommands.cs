using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trading.Application.Backtesting;
using Trading.Backtesting.Robustness;

namespace Trading.Api;

public static class RobustnessSuiteCommands
{
    private static readonly JsonSerializerOptions Json = CreateJson();
    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] == "run-robustness-suite";

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var command = Parse(args);
            var run = await ReadAsync<BacktestRun>(command.RunPath, 32 * 1024 * 1024, cancellationToken);
            var settings = command.SettingsPath is null ? new RobustnessSuiteSettings() :
                await ReadAsync<RobustnessSuiteSettings>(command.SettingsPath, 64 * 1024, cancellationToken);
            var artifact = RobustnessSuiteAnalyzer.Analyze(run, settings);
            await WriteNewAtomicallyAsync(command.OutputPath,
                RobustnessSuiteArtifactCodec.Serialize(artifact), cancellationToken);
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                status = "robustness-suite-created",
                artifact.SourceResultSha256,
                artifact.OriginalTradeCount,
                artifact.Settings.Iterations,
                artifact.ArtifactSha256,
                output = command.OutputPath
            }));
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { await error.WriteLineAsync("Robustness suite cancelled."); return 130; }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or
                                           IOException or JsonException or UnauthorizedAccessException)
        { await error.WriteLineAsync(exception.Message); return 2; }
    }

    private static Command Parse(string[] args)
    {
        string? run = null, output = null, settings = null;
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {args[index]}.");
            switch (args[index])
            {
                case "--run" when run is null: run = args[index + 1]; break;
                case "--output" when output is null: output = args[index + 1]; break;
                case "--settings" when settings is null: settings = args[index + 1]; break;
                default: throw new ArgumentException($"Unknown or duplicate option: {args[index]}");
            }
        }
        var runPath = JsonPath(run, "--run", true);
        var outputPath = JsonPath(output, "--output", false);
        if (File.Exists(outputPath)) throw new IOException("The robustness output file already exists.");
        return new(runPath, outputPath, settings is null ? null : JsonPath(settings, "--settings", true));
    }

    private static string JsonPath(string? value, string option, bool mustExist)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{option} is required.");
        var path = Path.GetFullPath(value);
        if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{option} must use .json.");
        if (mustExist && !File.Exists(path)) throw new FileNotFoundException($"{option} file was not found.", path);
        if (!mustExist && !Directory.Exists(Path.GetDirectoryName(path)))
            throw new ArgumentException("The output directory does not exist.");
        return path;
    }

    private static async Task<T> ReadAsync<T>(string path, long maximumBytes, CancellationToken token)
    {
        if (new FileInfo(path).Length is < 2 || new FileInfo(path).Length > maximumBytes)
            throw new InvalidDataException("Robustness input file is empty or exceeds its size limit.");
        return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, token), Json) ??
            throw new InvalidDataException("Robustness input JSON is empty.");
    }

    private static async Task WriteNewAtomicallyAsync(string path, string content, CancellationToken token)
    {
        var temporary = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), token);
            File.Move(temporary, path, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false));
        return options;
    }

    private sealed record Command(string RunPath, string OutputPath, string? SettingsPath);
}
