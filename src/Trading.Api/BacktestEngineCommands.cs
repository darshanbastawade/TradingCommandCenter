using System.Text;
using System.Text.Json;
using Trading.Application.Backtesting;

namespace Trading.Api;

public static class BacktestEngineCommands
{
    private const int MaximumBytes = 1024 * 1024;
    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] == "run-backtest-spec";

    public static async Task<int> RunAsync(string[] args, IServiceProvider services, TextWriter output,
        TextWriter error, CancellationToken cancellationToken = default)
    {
        string? createdFile = null;
        try
        {
            var command = Parse(args);
            var info = new FileInfo(command.Input);
            if (info.Length > MaximumBytes) throw new ArgumentException("Sealed backtest specification exceeds 1 MiB.");
            var json = await File.ReadAllTextAsync(command.Input, cancellationToken);
            var specification = BacktestSpecificationCodec.DeserializeSealed(json);
            if (!BacktestSpecificationCodec.Verify(specification))
                throw new ArgumentException("The sealed backtest specification hash is invalid.");
            await using var scope = services.CreateAsyncScope();
            var engine = scope.ServiceProvider.GetServices<IBacktestEngine>()
                .SingleOrDefault(item => item.EngineId == command.EngineId) ??
                throw new ArgumentException($"Unknown backtest engine: {command.EngineId}");
            var run = await engine.RunAsync(specification, cancellationToken);
            if (!BacktestRunCodec.Verify(run)) throw new InvalidDataException("The backtest engine returned invalid run evidence.");
            await WriteNewAtomicallyAsync(command.Output, BacktestRunCodec.Serialize(run), cancellationToken);
            createdFile = command.Output;
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                status = "backtest-run-created",
                engineId = run.EngineId,
                engineVersion = run.EngineVersion,
                specificationSha256 = run.SpecificationSha256,
                resultSha256 = run.ResultSha256,
                trades = run.Trades.Count,
                netPnl = run.NetPnl,
                output = command.Output
            }));
            createdFile = null;
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Delete(createdFile); await error.WriteLineAsync("Backtest engine run cancelled."); return 130;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or
                                           InvalidOperationException or NotSupportedException or JsonException or
                                           IOException or UnauthorizedAccessException)
        {
            Delete(createdFile); await error.WriteLineAsync(exception.Message); return 2;
        }
        catch (Exception)
        {
            Delete(createdFile); await error.WriteLineAsync("Backtest engine run failed."); return 3;
        }
    }

    private static Command Parse(string[] args)
    {
        string? engine = null; string? input = null; string? output = null;
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {args[index]}.");
            switch (args[index])
            {
                case "--engine" when engine is null: engine = args[index + 1]; break;
                case "--file" when input is null: input = args[index + 1]; break;
                case "--output" when output is null: output = args[index + 1]; break;
                default: throw new ArgumentException($"Unknown or duplicate option: {args[index]}");
            }
        }
        if (string.IsNullOrWhiteSpace(engine)) throw new ArgumentException("--engine is required.");
        var inputPath = JsonPath(input, "--file", true);
        var outputPath = JsonPath(output, "--output", false);
        if (File.Exists(outputPath)) throw new IOException("The output file already exists.");
        return new(engine.Trim(), inputPath, outputPath);
    }

    private static string JsonPath(string? value, string option, bool mustExist)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{option} is required.");
        var direct = Path.GetFullPath(value);
        var path = Path.IsPathRooted(value) || File.Exists(direct) ? direct : FromRepositoryRoot(value, direct);
        if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{option} must use .json.");
        if (mustExist && !File.Exists(path)) throw new FileNotFoundException($"The {option} file was not found.", path);
        if (!mustExist && !Directory.Exists(Path.GetDirectoryName(path)))
            throw new ArgumentException("The output directory does not exist.");
        return path;
    }

    private static string FromRepositoryRoot(string value, string fallback)
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "TradingCommandCenter.sln")))
                return Path.GetFullPath(value, directory.FullName);
        return fallback;
    }

    private static async Task WriteNewAtomicallyAsync(string path, string value, CancellationToken token)
    {
        var temporary = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(temporary, value, new UTF8Encoding(false), token);
            File.Move(temporary, path, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Delete(string? path) { if (path is not null && File.Exists(path)) File.Delete(path); }
    private sealed record Command(string EngineId, string Input, string Output);
}
