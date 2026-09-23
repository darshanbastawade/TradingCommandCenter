using Trading.Application.Backtesting;

namespace Trading.Backtesting.Robustness;

public static class RobustnessSuiteAnalyzer
{
    public static RobustnessSuiteArtifact Analyze(BacktestRun run, RobustnessSuiteSettings? settings = null,
        DateTime? createdAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        settings ??= new();
        Validate(run, settings);
        var pnl = run.Trades.Select(item => item.NetPnl).ToArray();
        var monteCarlo = Simulate(settings.Iterations, settings.Seed ^ 0x4D4F4E5445434152UL,
            run.InitialCapital, settings.RuinEquityFraction, iteration => Permute(pnl, iteration));
        var bootstrap = Simulate(settings.Iterations, settings.Seed ^ 0x424F4F5453545241UL,
            run.InitialCapital, settings.RuinEquityFraction, iteration => Bootstrap(pnl, iteration));
        var slippage = settings.AdditionalSlippageBasisPointsPerSide.Select(value => Scenario(
            $"additional-slippage-{value:0.####}-bps-per-side", value, "basisPointsPerSide", run,
            trade => Turnover(trade) * value / 10_000m)).ToArray();
        var costs = settings.CostMultipliers.Select(value => Scenario(
            $"recorded-cost-{value:0.####}x", value, "multiplier", run,
            trade => trade.Costs * (value - 1m))).ToArray();
        var delay = settings.EntryDelaySeconds.Select(value => Scenario(
            $"entry-delay-{value}-seconds", value, "seconds", run,
            trade => trade.EntryPrice * trade.Quantity * value *
                settings.EntryDelayPenaltyBasisPointsPerSecond / 10_000m)).ToArray();
        var missed = settings.MissedTradeProbabilities.Select((probability, index) =>
            new MissedTradeSimulationResult(probability, Simulate(settings.Iterations,
                settings.Seed ^ 0x4D49535345445452UL ^ (ulong)(index + 1), run.InitialCapital,
                settings.RuinEquityFraction, random => Miss(run.Trades, probability, random))))
            .ToArray();
        var timestamp = createdAtUtc ?? DateTime.UtcNow;
        if (timestamp.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Artifact creation time must be UTC.", nameof(createdAtUtc));
        return RobustnessSuiteArtifactCodec.Seal(new(1, timestamp, run.EngineId, run.EngineVersion,
            run.ResultSha256, run.SpecificationSha256, run.DeclaredDatasetSha256,
            run.ConsumedMarketDataSha256, run.Trades.Count, run.InitialCapital, settings,
            monteCarlo, bootstrap, Array.AsReadOnly(slippage), Array.AsReadOnly(costs),
            Array.AsReadOnly(delay), Array.AsReadOnly(missed), string.Empty));
    }

    private static void Validate(BacktestRun run, RobustnessSuiteSettings settings)
    {
        if (!BacktestRunCodec.Verify(run))
            throw new InvalidDataException("Robustness suite requires a verified backtest run.");
        if (run.EngineId != "native-csharp" || run.EngineRole != BacktestEngineRole.Authoritative)
            throw new ArgumentException("Robustness suite requires an authoritative native-csharp run.");
        if (run.Trades.Count == 0)
            throw new ArgumentException("Robustness suite requires at least one closed trade.");
        if (run.Trades.Count > 100_000 || settings.Iterations is < 100 or > 100_000 ||
            settings.RuinEquityFraction is <= 0 or >= 1 || settings.EntryDelayPenaltyBasisPointsPerSecond < 0 ||
            settings.EntryDelayPenaltyBasisPointsPerSecond > 100 ||
            !Valid(settings.AdditionalSlippageBasisPointsPerSide, value => value is >= 0 and <= 10_000) ||
            !Valid(settings.CostMultipliers, value => value is >= 1 and <= 100) ||
            !Valid(settings.MissedTradeProbabilities, value => value is >= 0 and < 1) ||
            settings.EntryDelaySeconds is null || settings.EntryDelaySeconds.Count is 0 or > 100 ||
            settings.EntryDelaySeconds.Any(value => value is < 0 or > 86400) ||
            settings.EntryDelaySeconds.Distinct().Count() != settings.EntryDelaySeconds.Count ||
            !settings.AdditionalSlippageBasisPointsPerSide.Contains(0) ||
            !settings.CostMultipliers.Contains(1) || !settings.EntryDelaySeconds.Contains(0) ||
            !settings.MissedTradeProbabilities.Contains(0) ||
            (long)settings.Iterations * run.Trades.Count *
                (2L + settings.MissedTradeProbabilities.Count) > 100_000_000)
            throw new ArgumentException("Robustness suite settings are invalid.", nameof(settings));
    }

    private static bool Valid(IReadOnlyList<decimal>? values, Func<decimal, bool> predicate) =>
        values is { Count: > 0 and <= 100 } && values.All(predicate) && values.Distinct().Count() == values.Count;

    private static RobustnessScenarioResult Scenario(string id, decimal value, string unit,
        BacktestRun run, Func<BacktestRunTrade, decimal> adversePenalty)
    {
        var adjusted = run.Trades.Select(trade => trade.NetPnl - adversePenalty(trade)).ToArray();
        var path = Path(adjusted, run.InitialCapital, 0);
        return new(id, value, unit, adjusted.Length, adjusted.Sum(),
            run.InitialCapital + adjusted.Sum(), path.MaximumDrawdownPercent, path.MaximumConsecutiveLosses);
    }

    private static decimal Turnover(BacktestRunTrade trade) =>
        (trade.EntryPrice + trade.ExitPrice) * trade.Quantity;

    private static RobustnessPathDistribution Simulate(int iterations, ulong seed, decimal initialCapital,
        decimal ruinEquityFraction, Func<DeterministicRandom, decimal[]> generator)
    {
        var pnls = new decimal[iterations];
        var drawdowns = new decimal[iterations];
        var losingStreaks = new decimal[iterations];
        var included = new decimal[iterations];
        var losses = 0;
        var ruins = 0;
        var random = new DeterministicRandom(seed);
        for (var index = 0; index < iterations; index++)
        {
            var trades = generator(random);
            var path = Path(trades, initialCapital, ruinEquityFraction);
            pnls[index] = path.NetPnl;
            drawdowns[index] = path.MaximumDrawdownPercent;
            losingStreaks[index] = path.MaximumConsecutiveLosses;
            included[index] = trades.Length;
            if (path.NetPnl < 0) losses++;
            if (path.Ruin) ruins++;
        }
        Array.Sort(pnls);
        Array.Sort(drawdowns);
        Array.Sort(losingStreaks);
        Array.Sort(included);
        return new(iterations, Percentile(pnls, .05m), Percentile(pnls, .50m),
            Percentile(pnls, .95m), Percentile(drawdowns, .95m),
            Percentile(losingStreaks, .95m),
            (decimal)losses / iterations, (decimal)ruins / iterations, Percentile(included, .50m));
    }

    private static decimal[] Permute(decimal[] source, DeterministicRandom random)
    {
        var result = (decimal[])source.Clone();
        for (var index = result.Length - 1; index > 0; index--)
        {
            var swap = random.Next(index + 1);
            (result[index], result[swap]) = (result[swap], result[index]);
        }
        return result;
    }

    private static decimal[] Bootstrap(decimal[] source, DeterministicRandom random)
    {
        var result = new decimal[source.Length];
        for (var index = 0; index < result.Length; index++) result[index] = source[random.Next(source.Length)];
        return result;
    }

    private static decimal[] Miss(IReadOnlyList<BacktestRunTrade> source, decimal probability,
        DeterministicRandom random)
    {
        var retained = new List<decimal>(source.Count);
        foreach (var trade in source)
            if (random.NextFraction() >= probability) retained.Add(trade.NetPnl);
        return retained.ToArray();
    }

    private static PathResult Path(IReadOnlyList<decimal> pnls, decimal initialCapital,
        decimal ruinEquityFraction)
    {
        var capital = initialCapital;
        var peak = initialCapital;
        var maximumDrawdown = 0m;
        var losingStreak = 0;
        var maximumLosingStreak = 0;
        var ruin = false;
        foreach (var pnl in pnls)
        {
            capital += pnl;
            peak = Math.Max(peak, capital);
            var drawdown = peak <= 0 ? 100m : Math.Max(0, (peak - capital) / peak * 100m);
            maximumDrawdown = Math.Max(maximumDrawdown, drawdown);
            losingStreak = pnl < 0 ? losingStreak + 1 : 0;
            maximumLosingStreak = Math.Max(maximumLosingStreak, losingStreak);
            if (ruinEquityFraction > 0 && capital <= initialCapital * ruinEquityFraction) ruin = true;
        }
        return new(capital - initialCapital, maximumDrawdown, maximumLosingStreak, ruin);
    }

    private static decimal Percentile(IReadOnlyList<decimal> sorted, decimal percentile)
    {
        if (sorted.Count == 0) return 0;
        var position = (sorted.Count - 1) * percentile;
        var lower = (int)decimal.Floor(position);
        var upper = (int)decimal.Ceiling(position);
        if (lower == upper) return sorted[lower];
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private sealed record PathResult(decimal NetPnl, decimal MaximumDrawdownPercent,
        int MaximumConsecutiveLosses, bool Ruin);

    private sealed class DeterministicRandom(ulong state)
    {
        private ulong _state = state;
        private ulong NextUInt64()
        {
            _state += 0x9E3779B97F4A7C15UL;
            var value = _state;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
        public int Next(int upperExclusive)
        {
            if (upperExclusive < 1) throw new ArgumentOutOfRangeException(nameof(upperExclusive));
            return (int)(NextUInt64() % (uint)upperExclusive);
        }
        public decimal NextFraction() => (NextUInt64() >> 11) / 9007199254740992m;
    }
}
