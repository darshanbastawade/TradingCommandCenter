using System.Text.Json;
using System.Text.Json.Serialization;
using Trading.Risk.Policy;

namespace Trading.Api;

public static class RiskPolicyCommands
{
    private const int MaximumBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] == "evaluate-risk";

    public static async Task<int> RunAsync(string[] args, IConfiguration configuration, TextWriter output,
        TextWriter error, CancellationToken cancellationToken = default)
    {
        try
        {
            var path = ResolveInputPath(ParseFile(args));
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumBytes) throw new ArgumentException("Risk input exceeds 1 MiB.");
            var input = await JsonSerializer.DeserializeAsync<RiskEvaluationInput>(stream, JsonOptions, cancellationToken) ??
                throw new ArgumentException("Risk input JSON is empty.");
            var settings = configuration.GetSection("RiskPolicy").Get<DeterministicRiskPolicySettings>() ?? new();
            var state = new RiskPortfolioState(input.AvailableCash, input.KillSwitchEngaged,
                new HashSet<string>(input.QualifiedStrategyIds ?? [], StringComparer.Ordinal),
                input.ClosedTrades ?? [], input.OpenPositions ?? []);
            var zone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            var decision = DeterministicRiskPolicy.EvaluateAndSize(settings, state,
                input.Request ?? throw new ArgumentException("A risk request is required."), zone);
            await output.WriteLineAsync(JsonSerializer.Serialize(decision, JsonOptions));
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("Risk evaluation cancelled.");
            return 130;
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync(exception.Message);
            return 2;
        }
    }

    private static string ParseFile(string[] args)
    {
        if (args.Length != 3 || args[1] != "--file" || string.IsNullOrWhiteSpace(args[2]))
            throw new ArgumentException("Usage: evaluate-risk --file <risk-input.json>");
        return args[2];
    }

    private static string ResolveInputPath(string value)
    {
        var direct = Path.GetFullPath(value);
        if (Path.IsPathRooted(value) || File.Exists(direct)) return direct;
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "TradingCommandCenter.sln"))) continue;
            return Path.GetFullPath(value, directory.FullName);
        }
        return direct;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public sealed record RiskEvaluationInput(decimal AvailableCash, bool KillSwitchEngaged,
        string[]? QualifiedStrategyIds, ClosedRiskTrade[]? ClosedTrades,
        OpenRiskPosition[]? OpenPositions, TradeSizingRiskRequest? Request);
}
