using System.Text;
using System.Text.Json;
using Trading.Application.Backtesting;

namespace Trading.Api;

public static class BacktestSpecificationCommands
{
    private const int MaximumBytes = 1024 * 1024;
    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] == "seal-backtest-spec";

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default)
    {
        string? createdFile = null;
        try
        {
            var command = Parse(args);
            var info = new FileInfo(command.Input);
            if (info.Length > MaximumBytes) throw new ArgumentException("Backtest specification exceeds 1 MiB.");
            var json = await File.ReadAllTextAsync(command.Input, cancellationToken);
            var document = BacktestSpecificationCodec.Seal(BacktestSpecificationCodec.Deserialize(json));
            var sealedJson = BacktestSpecificationCodec.Serialize(document);
            await WriteNewAtomicallyAsync(command.Output, sealedJson, cancellationToken);
            createdFile = command.Output;
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                status = "backtest-specification-sealed",
                strategyId = document.Specification.StrategyId,
                instrumentId = document.Specification.Instrument.InstrumentId,
                fromUtc = document.Specification.Data.FromUtc,
                toUtc = document.Specification.Data.ToUtc,
                specificationSha256 = document.SpecificationSha256,
                output = command.Output
            }));
            createdFile = null;
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Delete(createdFile); await error.WriteLineAsync("Backtest specification sealing cancelled."); return 130;
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or IOException or UnauthorizedAccessException)
        {
            Delete(createdFile); await error.WriteLineAsync(exception.Message); return 2;
        }
        catch (Exception)
        {
            Delete(createdFile); await error.WriteLineAsync("Backtest specification sealing failed."); return 3;
        }
    }

    private static Command Parse(string[] args)
    {
        if (args.Length != 5) throw new ArgumentException("Usage: seal-backtest-spec --file <input.json> --output <sealed.json>");
        string? input = null; string? output = null;
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {args[index]}.");
            switch (args[index])
            {
                case "--file" when input is null: input = args[index + 1]; break;
                case "--output" when output is null: output = args[index + 1]; break;
                default: throw new ArgumentException($"Unknown or duplicate option: {args[index]}");
            }
        }
        var inputPath = JsonPath(input, "--file", true);
        var outputPath = JsonPath(output, "--output", false);
        if (File.Exists(outputPath)) throw new IOException("The output file already exists.");
        return new(inputPath, outputPath);
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
    private sealed record Command(string Input, string Output);
}
