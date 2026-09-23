using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trading.Application.AI;
using Trading.Backtesting.Certification;

namespace Trading.Api;

public static class QualifiedStrategyCommands
{
    private static readonly JsonSerializerOptions Json = Options();
    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] == "qualify-strategy";

    public static async Task<int> RunAsync(string[] args, IConfiguration configuration, TextWriter output,
        TextWriter error, CancellationToken cancellationToken = default)
    {
        try
        {
            var command = Parse(args);
            var certificate = await ReadAsync<StrategyCertificateV2>(command.Certificate, cancellationToken);
            var analysis = await ReadAsync<AstraResearchAnalysisV2Artifact>(command.Analysis, cancellationToken);
            var policy = configuration.GetSection("QualifiedStrategyPipeline").Get<QualifiedStrategyPolicy>() ?? new();
            var artifact = QualifiedStrategyPipeline.Qualify(certificate, analysis,
                command.OperatorApprovalReference, DateTime.UtcNow, policy);
            await WriteAsync(command.Output, QualifiedStrategyPipeline.Serialize(artifact), cancellationToken);
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                status = "strategy-qualified-for-paper-qualification", artifact.QualificationId,
                artifact.StrategyId, artifact.ExpiresAtUtc, artifact.AnalystRecommendation,
                artifact.QualificationSha256, output = command.Output
            }, Json));
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { await error.WriteLineAsync("Strategy qualification cancelled."); return 130; }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or
                                           InvalidOperationException or IOException or JsonException or
                                           UnauthorizedAccessException)
        { await error.WriteLineAsync(exception.Message); return 2; }
    }

    private static Command Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {args[index]}.");
            if (args[index] is not ("--certificate" or "--analysis" or "--operator-approval-reference" or
                "--output") || !values.TryAdd(args[index], args[index + 1]))
                throw new ArgumentException($"Unknown or duplicate option: {args[index]}");
        }
        return new(Input(values, "--certificate"), Input(values, "--analysis"),
            Required(values, "--operator-approval-reference"), Output(values));
    }
    private static string Required(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value :
            throw new ArgumentException($"{key} is required.");
    private static string Input(IReadOnlyDictionary<string, string> values, string key)
    {
        var path = JsonPath(Required(values, key), key);
        if (!File.Exists(path)) throw new FileNotFoundException($"{key} file was not found.", path);
        return path;
    }
    private static string Output(IReadOnlyDictionary<string, string> values)
    {
        var path = JsonPath(Required(values, "--output"), "--output");
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw new ArgumentException("Output directory does not exist.");
        if (File.Exists(path)) throw new IOException("Qualification output already exists.");
        return path;
    }
    private static string JsonPath(string value, string option)
    {
        var path = Path.GetFullPath(value);
        if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{option} must use .json.");
        return path;
    }
    private static async Task<T> ReadAsync<T>(string path, CancellationToken token)
    {
        var length = new FileInfo(path).Length;
        if (length is < 2 or > 4 * 1024 * 1024) throw new InvalidDataException("Qualification input is empty or too large.");
        return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, token), Json) ??
            throw new InvalidDataException("Qualification input JSON is empty.");
    }
    private static async Task WriteAsync(string path, string value, CancellationToken token)
    {
        var temporary = $"{path}.tmp-{Guid.NewGuid():N}";
        try { await File.WriteAllTextAsync(temporary, value, new UTF8Encoding(false), token); File.Move(temporary, path); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false));
        return options;
    }
    private sealed record Command(string Certificate, string Analysis,
        string OperatorApprovalReference, string Output);
}
