namespace Trading.ExternalValidation.Lean;

public sealed record LeanOptions
{
    public string CliExecutable { get; init; } = "lean";
    public string ProjectDirectory { get; init; } = string.Empty;
    public string DataDirectory { get; init; } = string.Empty;
    public string Image { get; init; } = string.Empty;
    public int TimeoutSeconds { get; init; } = 1800;
}

public sealed record LeanMappedInput(
    string RunId,
    string RequestPath,
    string CandlePath,
    string EvidencePath,
    string OutputDirectory,
    string ConsumedMarketDataSha256,
    int CandleCount);

public sealed record LeanProcessResult(string OfficialResultPath, string EvidencePath);

public interface ILeanProcessRunner
{
    Task<LeanProcessResult> RunAsync(LeanMappedInput input, CancellationToken cancellationToken);
}
