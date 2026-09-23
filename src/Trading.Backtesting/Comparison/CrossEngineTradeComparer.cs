using Trading.Application.Backtesting;

namespace Trading.Backtesting.Comparison;

public static class CrossEngineTradeComparer
{
    private const int MaximumTrades = 10_000;

    public static CrossEngineComparison Compare(BacktestRun native, BacktestRun lean,
        CrossEngineComparisonPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(lean);
        policy ??= new CrossEngineComparisonPolicy();
        Validate(native, lean, policy);

        var usedLean = new bool[lean.Trades.Count];
        var assignments = Enumerable.Repeat(-1, native.Trades.Count).ToArray();
        // Reserve exact entry matches before allowing near-time matches to use those trades.
        for (var index = 0; index < native.Trades.Count; index++)
        {
            var match = FindNearest(native.Trades[index], lean.Trades, usedLean, 0);
            if (match < 0) continue;
            assignments[index] = match;
            usedLean[match] = true;
        }
        for (var index = 0; index < native.Trades.Count; index++)
        {
            if (assignments[index] >= 0) continue;
            var match = FindNearest(native.Trades[index], lean.Trades, usedLean,
                policy.EntryMatchWindowSeconds);
            if (match < 0) continue;
            assignments[index] = match;
            usedLean[match] = true;
        }
        var comparisons = new List<CrossEngineTradeComparison>(Math.Max(native.Trades.Count, lean.Trades.Count));
        var matched = 0;
        var matchingEntries = 0;
        var matchingExits = 0;
        var maxEntryPriceBps = 0m;
        var maxExitPriceBps = 0m;
        var structuralMismatch = false;

        for (var nativeIndex = 0; nativeIndex < native.Trades.Count; nativeIndex++)
        {
            var source = native.Trades[nativeIndex];
            var leanIndex = assignments[nativeIndex];
            if (leanIndex < 0)
            {
                comparisons.Add(new(nativeIndex, null, source, null, null, null, null, null, null,
                    ["missingLeanTrade"]));
                continue;
            }

            matched++;
            var target = lean.Trades[leanIndex];
            var entrySeconds = (decimal)(target.EntryTimeUtc - source.EntryTimeUtc).TotalSeconds;
            var exitSeconds = (decimal)(target.ExitTimeUtc - source.ExitTimeUtc).TotalSeconds;
            var entryBps = BasisPoints(source.EntryPrice, target.EntryPrice);
            var exitBps = BasisPoints(source.ExitPrice, target.ExitPrice);
            maxEntryPriceBps = Math.Max(maxEntryPriceBps, entryBps);
            maxExitPriceBps = Math.Max(maxExitPriceBps, exitBps);
            if (Math.Abs(entrySeconds) <= policy.TimestampToleranceSeconds) matchingEntries++;
            if (Math.Abs(exitSeconds) <= policy.TimestampToleranceSeconds) matchingExits++;
            if (source.Quantity != target.Quantity ||
                Math.Abs((decimal)(target.SignalTimeUtc - source.SignalTimeUtc).TotalSeconds) >
                    policy.TimestampToleranceSeconds ||
                BasisPoints(source.StopPrice, target.StopPrice) > policy.PriceToleranceBasisPoints ||
                BasisPoints(source.TargetPrice, target.TargetPrice) > policy.PriceToleranceBasisPoints ||
                source.ExitReason != target.ExitReason || source.Costs != target.Costs)
                structuralMismatch = true;

            var differences = new List<string>();
            if (Math.Abs((decimal)(target.SignalTimeUtc - source.SignalTimeUtc).TotalSeconds) >
                policy.TimestampToleranceSeconds) differences.Add("signalTime");
            if (Math.Abs(entrySeconds) > policy.TimestampToleranceSeconds) differences.Add("entryTime");
            if (Math.Abs(exitSeconds) > policy.TimestampToleranceSeconds) differences.Add("exitTime");
            if (source.Quantity != target.Quantity) differences.Add("quantity");
            if (entryBps > policy.PriceToleranceBasisPoints) differences.Add("entryPrice");
            if (exitBps > policy.PriceToleranceBasisPoints) differences.Add("exitPrice");
            if (BasisPoints(source.StopPrice, target.StopPrice) > policy.PriceToleranceBasisPoints)
                differences.Add("stopPrice");
            if (BasisPoints(source.TargetPrice, target.TargetPrice) > policy.PriceToleranceBasisPoints)
                differences.Add("targetPrice");
            if (source.ExitReason != target.ExitReason) differences.Add("exitReason");
            if (source.GrossPnl != target.GrossPnl) differences.Add("grossPnl");
            if (source.Costs != target.Costs) differences.Add("costs");
            if (source.NetPnl != target.NetPnl) differences.Add("netPnl");
            comparisons.Add(new(nativeIndex, leanIndex, source, target, entrySeconds, exitSeconds,
                entryBps, exitBps, target.NetPnl - source.NetPnl, differences));
        }

        for (var leanIndex = 0; leanIndex < usedLean.Length; leanIndex++)
            if (!usedLean[leanIndex])
                comparisons.Add(new(null, leanIndex, null, lean.Trades[leanIndex], null, null, null,
                    null, null, ["extraLeanTrade"]));

        var matchedRate = Ratio(matched, Math.Max(native.Trades.Count, lean.Trades.Count));
        var entryRate = Ratio(matchingEntries, matched);
        var exitRate = Ratio(matchingExits, matched);
        var pnlDeviation = PercentageDeviation(native.NetPnl, lean.NetPnl);
        var nativeDrawdown = ClosedTradeDrawdown(native);
        var leanDrawdown = ClosedTradeDrawdown(lean);
        var drawdownDeviation = Math.Abs(nativeDrawdown - leanDrawdown);
        var verdict = native.Trades.Count == 0 && lean.Trades.Count == 0
            ? CrossEngineVerdict.NoTrades
            : matchedRate >= policy.MinimumMatchedTradeRate &&
              entryRate == 1m && exitRate == 1m && !structuralMismatch &&
              maxEntryPriceBps <= policy.PriceToleranceBasisPoints &&
              maxExitPriceBps <= policy.PriceToleranceBasisPoints &&
              pnlDeviation <= policy.MaximumNetPnlDeviationPercent &&
              drawdownDeviation <= policy.MaximumDrawdownDeviationPercentagePoints
                ? CrossEngineVerdict.Pass : CrossEngineVerdict.Divergent;

        return CrossEngineComparisonCodec.Seal(new(1, native.SpecificationSha256,
            native.DeclaredDatasetSha256, native.ConsumedMarketDataSha256,
            native.EngineId, native.EngineVersion, native.ResultSha256,
            lean.EngineId, lean.EngineVersion, lean.ResultSha256, policy, verdict,
            native.Trades.Count, lean.Trades.Count, matched, matchedRate, entryRate, exitRate,
            maxEntryPriceBps, maxExitPriceBps, native.NetPnl, lean.NetPnl, pnlDeviation,
            nativeDrawdown, leanDrawdown, drawdownDeviation, comparisons, string.Empty));
    }

    private static void Validate(BacktestRun native, BacktestRun lean, CrossEngineComparisonPolicy policy)
    {
        if (!BacktestRunCodec.Verify(native) || !BacktestRunCodec.Verify(lean))
            throw new InvalidDataException("Both backtest runs must have valid sealed evidence.");
        if (native.EngineId != "native-csharp" || native.EngineRole != BacktestEngineRole.Authoritative ||
            lean.EngineId != "lean" || lean.EngineRole != BacktestEngineRole.IndependentValidation)
            throw new ArgumentException("Comparison requires authoritative native-csharp and independent lean runs.");
        if (native.SpecificationSha256 != lean.SpecificationSha256 ||
            native.DeclaredDatasetSha256 != lean.DeclaredDatasetSha256 ||
            native.ConsumedMarketDataSha256 != lean.ConsumedMarketDataSha256 ||
            native.InitialCapital != lean.InitialCapital)
            throw new InvalidDataException("Backtest runs do not represent the same specification, data and capital.");
        if (native.Trades.Count > MaximumTrades || lean.Trades.Count > MaximumTrades)
            throw new ArgumentException($"Comparison supports at most {MaximumTrades} trades per engine.");
        if (policy.EntryMatchWindowSeconds is < 0 or > 86400 ||
            policy.TimestampToleranceSeconds < 0 ||
            policy.TimestampToleranceSeconds > policy.EntryMatchWindowSeconds ||
            policy.PriceToleranceBasisPoints is < 0 or > 10_000 ||
            policy.MinimumMatchedTradeRate is < 0 or > 1 ||
            policy.MaximumNetPnlDeviationPercent is < 0 or > 100 ||
            policy.MaximumDrawdownDeviationPercentagePoints is < 0 or > 100)
            throw new ArgumentException("Cross-engine comparison policy is invalid.");
    }

    private static int FindNearest(BacktestRunTrade source, IReadOnlyList<BacktestRunTrade> candidates,
        IReadOnlyList<bool> used, int windowSeconds)
    {
        var best = -1;
        var bestEntryOffset = double.MaxValue;
        var bestExitOffset = double.MaxValue;
        for (var index = 0; index < candidates.Count; index++)
        {
            if (used[index]) continue;
            var candidate = candidates[index];
            if (source.StrategyId != candidate.StrategyId ||
                source.InstrumentId != candidate.InstrumentId || source.Direction != candidate.Direction)
                continue;
            var entryOffset = Math.Abs((candidate.EntryTimeUtc - source.EntryTimeUtc).TotalSeconds);
            if (entryOffset > windowSeconds) continue;
            var exitOffset = Math.Abs((candidate.ExitTimeUtc - source.ExitTimeUtc).TotalSeconds);
            if (entryOffset < bestEntryOffset || entryOffset == bestEntryOffset && exitOffset < bestExitOffset)
            {
                best = index;
                bestEntryOffset = entryOffset;
                bestExitOffset = exitOffset;
            }
        }
        return best;
    }

    private static decimal BasisPoints(decimal source, decimal target) =>
        Math.Abs(target - source) / source * 10_000m;

    private static decimal Ratio(int numerator, int denominator) => denominator == 0 ? 0 :
        (decimal)numerator / denominator;

    private static decimal PercentageDeviation(decimal source, decimal target) =>
        Math.Abs(target - source) / Math.Max(1m, Math.Max(Math.Abs(source), Math.Abs(target))) * 100m;

    private static decimal ClosedTradeDrawdown(BacktestRun run)
    {
        var peak = run.InitialCapital;
        var maximum = 0m;
        foreach (var trade in run.Trades)
        {
            peak = Math.Max(peak, trade.CapitalAfterTrade);
            maximum = Math.Max(maximum, (peak - trade.CapitalAfterTrade) / peak * 100m);
        }
        return maximum;
    }
}
