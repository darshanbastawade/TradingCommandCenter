using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trading.Application.Backtesting;
using Trading.Backtesting.Comparison;

namespace Trading.Api;

public static class CrossEngineComparisonCommands
{
    private const long MaximumRunBytes = 32L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = CreateJson();

    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] == "compare-backtest-runs";

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var command = Parse(args);
            var native = await ReadRunAsync(command.NativePath, cancellationToken);
            var lean = await ReadRunAsync(command.LeanPath, cancellationToken);
            var policy = command.PolicyPath is null ? new CrossEngineComparisonPolicy() :
                await ReadPolicyAsync(command.PolicyPath, cancellationToken);
            var comparison = CrossEngineTradeComparer.Compare(native, lean, policy);
            await WriteNewAtomicallyAsync(command.OutputPath,
                CrossEngineComparisonCodec.Serialize(comparison), cancellationToken);
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                status = "cross-engine-comparison-created",
                verdict = comparison.Verdict.ToString(),
                comparison.MatchedTradeCount,
                comparison.NativeTradeCount,
                comparison.LeanTradeCount,
                comparison.ComparisonSha256,
                output = command.OutputPath
            }));
            return comparison.Verdict == CrossEngineVerdict.Pass ? 0 : 4;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { await error.WriteLineAsync("Cross-engine comparison cancelled."); return 130; }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or
                                           IOException or JsonException or UnauthorizedAccessException)
        { await error.WriteLineAsync(exception.Message); return 2; }
    }

    private static Command Parse(string[] args)
    {
        string? native = null, lean = null, output = null, policy = null;
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {args[index]}.");
            switch (args[index])
            {
                case "--native" when native is null: native = args[index + 1]; break;
                case "--lean" when lean is null: lean = args[index + 1]; break;
                case "--output" when output is null: output = args[index + 1]; break;
                case "--policy" when policy is null: policy = args[index + 1]; break;
                default: throw new ArgumentException($"Unknown or duplicate option: {args[index]}");
            }
        }
        var nativePath = JsonPath(native, "--native", true);
        var leanPath = JsonPath(lean, "--lean", true);
        var outputPath = JsonPath(output, "--output", false);
        if (File.Exists(outputPath)) throw new IOException("The comparison output file already exists.");
        return new(nativePath, leanPath, outputPath,
            policy is null ? null : JsonPath(policy, "--policy", true));
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

    private static async Task<BacktestRun> ReadRunAsync(string path, CancellationToken token)
    {
        if (new FileInfo(path).Length is < 2 or > MaximumRunBytes)
            throw new InvalidDataException("Backtest run file is empty or exceeds 32 MiB.");
        var run = JsonSerializer.Deserialize<BacktestRun>(await File.ReadAllTextAsync(path, token), Json) ??
            throw new InvalidDataException("Backtest run JSON is empty.");
        if (!BacktestRunCodec.Verify(run)) throw new InvalidDataException("Backtest run hash or ledger is invalid.");
        return run;
    }

    private static async Task<CrossEngineComparisonPolicy> ReadPolicyAsync(string path, CancellationToken token)
    {
        if (new FileInfo(path).Length is < 2 or > 64 * 1024)
            throw new InvalidDataException("Comparison policy file is empty or exceeds 64 KiB.");
        return JsonSerializer.Deserialize<CrossEngineComparisonPolicy>(
            await File.ReadAllTextAsync(path, token), Json) ??
            throw new InvalidDataException("Comparison policy JSON is empty.");
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

    private sealed record Command(string NativePath, string LeanPath, string OutputPath, string? PolicyPath);
}
