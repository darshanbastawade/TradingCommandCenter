using System.Text;
using System.Text.Json;
using Trading.Application.Backtesting;
using Trading.Application.MarketData;
using Trading.Application.Research;
using Trading.Backtesting.Research;
using Trading.Domain.MarketData;
using Trading.Domain.Research;

namespace Trading.Api;

public static class ResearchCandidateCommands
{
    private const int MaximumInputBytes = 1024 * 1024;
    public static bool IsCommand(string[] args) => args.Length > 0 &&
        args[0] is "sweep-parameters" or "verify-backtest-candidates";

    public static async Task<int> RunAsync(string[] args, IServiceProvider services, TextWriter output,
        TextWriter error, CancellationToken cancellationToken = default)
    {
        string? createdFile = null;
        try
        {
            var values = Parse(args);
            var destination = Output(values); createdFile = destination;
            var result = args[0] == "sweep-parameters"
                ? await SweepAsync(values, destination, services, cancellationToken)
                : await VerifyAsync(values, destination, services, cancellationToken);
            await output.WriteLineAsync(result); createdFile = null; return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Delete(createdFile); await error.WriteLineAsync("Research candidate operation cancelled."); return 130;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException or
                                           InvalidDataException or InvalidOperationException or TimeoutException or
                                           JsonException or IOException or UnauthorizedAccessException)
        {
            Delete(createdFile); await error.WriteLineAsync(exception.Message); return 2;
        }
        catch (Exception)
        {
            Delete(createdFile); await error.WriteLineAsync("Research candidate operation failed."); return 3;
        }
    }

    private static async Task<string> SweepAsync(IReadOnlyDictionary<string, string> values, string destination,
        IServiceProvider services, CancellationToken token)
    {
        var specificationPath = Input(values, "file"); var gridPath = Input(values, "grid");
        var specification = BacktestSpecificationCodec.DeserializeSealed(
            await File.ReadAllTextAsync(specificationPath, token));
        var grid = ParameterSweepPlanner.Deserialize(await File.ReadAllTextAsync(gridPath, token));
        var plan = ParameterSweepPlanner.Create(specification, grid);
        var workerId = Value(values, "worker", "vectorbt");
        await using var scope = services.CreateAsyncScope();
        var worker = scope.ServiceProvider.GetServices<IResearchBacktestWorker>()
            .SingleOrDefault(item => item.WorkerId == workerId) ??
            throw new ArgumentException($"Unknown research worker: {workerId}");
        if (worker.Role != BacktestEngineRole.ResearchExploration)
            throw new InvalidOperationException("Parameter sweeps require a research-exploration worker.");
        var marketData = scope.ServiceProvider.GetRequiredService<IMarketDataStore>();
        var data = specification.Specification.Data;
        var candles = await ReadAllAsync(marketData, specification.Specification.Instrument.InstrumentId,
            (Timeframe)data.TimeframeMinutes, data.FromUtc, data.ToUtc, token);
        if (candles.Count < 2) throw new InvalidOperationException("At least two candles are required for a sweep.");
        var request = ResearchWorkerEvidenceCodec.Seal(new ResearchWorkerRequest(1, Guid.NewGuid().ToString("N"),
            string.Empty, specification.SpecificationSha256, specification.Specification.StrategyId,
            data.TimeframeMinutes, data.ExchangeTimeZoneId, specification.Specification.Capital.InitialCapital,
            specification.Specification.Execution.SlippageBasisPointsPerSide,
            specification.Specification.Execution.SessionExitTime, plan.TopCandidates,
            candles.Select(item => new ResearchWorkerCandle(item.OpenTimeUtc, item.Open, item.High, item.Low,
                item.Close, item.Volume)).ToArray(),
            plan.Candidates.Select(item => new ResearchWorkerCandidate(item.SpecificationSha256,
                item.Specification.Parameters)).ToArray()));
        var workerResult = await worker.RunAsync(request, token);
        if (!ResearchWorkerEvidenceCodec.Verify(workerResult, request))
            throw new InvalidDataException("Research worker evidence is invalid.");
        var specifications = plan.Candidates.ToDictionary(item => item.SpecificationSha256, StringComparer.Ordinal);
        var ordered = workerResult.Candidates.OrderByDescending(item => item.Metrics.Score)
            .ThenBy(item => item.CandidateKey, StringComparer.Ordinal).ToArray();
        var sweepId = Guid.NewGuid(); var createdAt = DateTime.UtcNow;
        var artifactCandidates = ordered.Select((item, index) =>
        {
            if (!specifications.TryGetValue(item.CandidateKey, out var candidateSpecification))
                throw new InvalidDataException("Research worker returned an unknown candidate.");
            return new ParameterSweepCandidateArtifact(Guid.NewGuid(), index + 1, candidateSpecification, item.Metrics);
        }).ToArray();
        var artifact = CandidateArtifactCodec.Seal(new ParameterSweepArtifact(1, sweepId, createdAt,
            worker.WorkerId, worker.WorkerVersion, specification.SpecificationSha256, data.DatasetSha256,
            plan.GridSha256, request.RequestSha256, workerResult.EvidenceSha256, plan.Candidates.Count,
            artifactCandidates, string.Empty));
        var artifactJson = CandidateArtifactCodec.Serialize(artifact);
        await WriteNewAsync(destination, artifactJson, token);
        var sweep = new ParameterSweep(sweepId, createdAt, specification.Specification.StrategyId,
            worker.WorkerId, worker.WorkerVersion, specification.SpecificationSha256, data.DatasetSha256,
            plan.GridSha256, plan.Candidates.Count, artifactCandidates.Length, artifact.ArtifactSha256, artifactJson);
        var candidates = artifactCandidates.Select(item => new BacktestCandidate(item.CandidateId, sweepId,
            item.Rank, item.Specification.Specification.StrategyId, item.Specification.SpecificationSha256,
            item.Specification.Specification.Data.DatasetSha256, BacktestSpecificationCodec.Serialize(item.Specification),
            JsonSerializer.Serialize(item.Specification.Specification.Parameters), item.Metrics.Score,
            JsonSerializer.Serialize(item.Metrics), workerResult.EvidenceSha256)).ToArray();
        await scope.ServiceProvider.GetRequiredService<IBacktestCandidateStore>()
            .AddSweepAsync(sweep, candidates, token);
        return JsonSerializer.Serialize(new { status = "parameter-sweep-created", sweepId,
            evaluatedCandidates = plan.Candidates.Count, storedCandidates = candidates.Length,
            artifactSha256 = artifact.ArtifactSha256, output = destination });
    }

    private static async Task<string> VerifyAsync(IReadOnlyDictionary<string, string> values, string destination,
        IServiceProvider services, CancellationToken token)
    {
        if (!Guid.TryParse(Required(values, "sweep-id"), out var sweepId) || sweepId == Guid.Empty)
            throw new ArgumentException("--sweep-id must be a non-empty GUID.");
        var limit = PositiveInt(values, "top", 20, 100);
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IBacktestCandidateStore>();
        var sweep = await store.FindSweepAsync(sweepId, token) ?? throw new ArgumentException("Parameter sweep was not found.");
        if (sweep.NativeVerificationCompleted) throw new InvalidOperationException("Sweep verification is already complete.");
        var engine = scope.ServiceProvider.GetServices<IBacktestEngine>()
            .SingleOrDefault(item => item.Role == BacktestEngineRole.Authoritative) ??
            throw new InvalidOperationException("Exactly one authoritative backtest engine must be registered.");
        var candidates = (await store.ListCandidatesAsync(sweepId, limit, token))
            .Where(item => item.Status == BacktestCandidateStatus.ResearchProposed).Take(limit).ToArray();
        if (candidates.Length == 0) throw new InvalidOperationException("No proposed candidates are available.");
        var outcomes = new List<NativeCandidateOutcome>(candidates.Length);
        var evidence = new List<NativeVerificationCandidateArtifact>(candidates.Length);
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested(); var verifiedAt = DateTime.UtcNow;
            try
            {
                var specification = BacktestSpecificationCodec.DeserializeSealed(candidate.CandidateSpecificationJson);
                if (!BacktestSpecificationCodec.Verify(specification) ||
                    specification.SpecificationSha256 != candidate.SpecificationSha256 ||
                    specification.Specification.Data.DatasetSha256 != sweep.DatasetSha256)
                    throw new InvalidDataException("Stored candidate specification evidence is invalid.");
                var run = await engine.RunAsync(specification, token);
                if (!BacktestRunCodec.Verify(run) || run.SpecificationSha256 != candidate.SpecificationSha256 ||
                    run.DeclaredDatasetSha256 != sweep.DatasetSha256)
                    throw new InvalidDataException("Authoritative engine evidence does not match the candidate.");
                var runJson = BacktestRunCodec.Serialize(run);
                outcomes.Add(new(candidate.Id, true, verifiedAt, engine.EngineId, engine.EngineVersion,
                    run.ResultSha256, run.NetPnl, run.Trades.Count, runJson, string.Empty));
                evidence.Add(new(candidate.Id, candidate.Rank, candidate.SpecificationSha256, true,
                    run.ResultSha256, run.NetPnl, run.Trades.Count, string.Empty));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or
                                               InvalidOperationException or NotSupportedException)
            {
                var failure = Safe(exception.Message);
                outcomes.Add(new(candidate.Id, false, verifiedAt, engine.EngineId, engine.EngineVersion,
                    string.Empty, null, 0, string.Empty, failure));
                evidence.Add(new(candidate.Id, candidate.Rank, candidate.SpecificationSha256, false,
                    string.Empty, null, null, failure));
            }
        }
        var verificationId = Guid.NewGuid(); var createdAt = DateTime.UtcNow;
        var artifact = CandidateArtifactCodec.Seal(new NativeVerificationArtifact(1, verificationId, sweepId,
            createdAt, engine.EngineId, engine.EngineVersion, evidence, string.Empty));
        var artifactJson = CandidateArtifactCodec.Serialize(artifact);
        await WriteNewAsync(destination, artifactJson, token);
        var verification = new NativeCandidateVerificationRun(verificationId, sweepId, createdAt,
            engine.EngineId, engine.EngineVersion, outcomes.Count, outcomes.Count(item => item.Succeeded),
            outcomes.Count(item => !item.Succeeded), artifact.ArtifactSha256, artifactJson);
        await store.CompleteVerificationAsync(verification, outcomes, token);
        return JsonSerializer.Serialize(new { status = "native-candidate-verification-complete", sweepId,
            verificationId, verified = verification.VerifiedCandidates, failed = verification.FailedCandidates,
            artifactSha256 = artifact.ArtifactSha256, output = destination });
    }

    private static async Task<IReadOnlyList<Candle>> ReadAllAsync(IMarketDataStore store, Guid instrumentId,
        Timeframe timeframe, DateTime fromUtc, DateTime toUtc, CancellationToken token)
    {
        var result = new List<Candle>(); var cursor = new DateTimeOffset(fromUtc); var end = new DateTimeOffset(toUtc);
        while (cursor < end)
        {
            var page = await store.ReadCandlesAsync(instrumentId, timeframe, cursor, end, 10_000, token);
            result.AddRange(page); if (page.Count < 10_000) break;
            cursor = new DateTimeOffset(page[^1].OpenTimeUtc.AddTicks(1));
        }
        return result;
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Missing value for {args[index]}.");
            if (!values.TryAdd(args[index][2..], args[index + 1]))
                throw new ArgumentException($"Duplicate option: {args[index]}");
        }
        return values;
    }

    private static string Input(IReadOnlyDictionary<string, string> values, string name)
    {
        var path = Path.GetFullPath(Required(values, name));
        if (!File.Exists(path)) throw new FileNotFoundException($"--{name} was not found.", path);
        if (new FileInfo(path).Length > MaximumInputBytes) throw new ArgumentException($"--{name} exceeds 1 MiB.");
        return path;
    }

    private static string Output(IReadOnlyDictionary<string, string> values)
    {
        var path = Path.GetFullPath(Required(values, "output"));
        if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("--output must use .json.");
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw new ArgumentException("Output directory does not exist.");
        if (File.Exists(path)) throw new IOException("The output file already exists.");
        return path;
    }

    private static int PositiveInt(IReadOnlyDictionary<string, string> values, string name, int fallback, int maximum)
    {
        var text = Value(values, name, fallback.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!int.TryParse(text, out var value) || value < 1 || value > maximum)
            throw new ArgumentException($"--{name} must be 1-{maximum}.");
        return value;
    }

    private static string Required(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() :
        throw new ArgumentException($"--{name} is required.");
    private static string Value(IReadOnlyDictionary<string, string> values, string name, string fallback) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : fallback;
    private static string Safe(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim()[..Math.Min(512,
        value.Replace('\r', ' ').Replace('\n', ' ').Trim().Length)];
    private static async Task WriteNewAsync(string path, string contents, CancellationToken token)
    {
        var temporary = $"{path}.tmp-{Guid.NewGuid():N}";
        try { await File.WriteAllTextAsync(temporary, contents, new UTF8Encoding(false), token); File.Move(temporary, path, false); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void Delete(string? path) { if (path is not null && File.Exists(path)) File.Delete(path); }
}
