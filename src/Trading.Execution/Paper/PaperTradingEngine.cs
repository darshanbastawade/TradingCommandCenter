using Trading.Application.MarketData;
using Trading.Risk.Policy;

namespace Trading.Execution.Paper;

public enum PaperOrderStatus { FilledAndClosed = 1, RiskRejected = 2, DataRejected = 3 }
public enum PaperExitReason { Stop = 1, Target = 2, PlannedExit = 3 }

public sealed record PaperTradeIntent(Guid RequestId, Guid InstrumentId, uint InstrumentToken,
    string Exchange, string TradingSymbol, DateTime SubmittedAtUtc, DateTime PlannedExitUtc,
    decimal StopPrice, decimal TargetPrice, int LotSize, int? MaximumLots,
    TradingCapitalPool CapitalPool = TradingCapitalPool.StrategyTesting);

public sealed record PaperTradingRequest(Guid SessionId, DateTime CreatedAtUtc, string StrategyId,
    decimal InitialCash, decimal SlippageBasisPoints, decimal FeeBasisPointsPerSide,
    decimal FixedFeePerFill, int MaximumTickAgeSeconds, bool KillSwitchEngaged,
    IReadOnlyList<PaperTradeIntent> Orders, IReadOnlyList<NormalizedMarketTick> Ticks);

public sealed record PaperTradeResult(Guid RequestId, PaperOrderStatus Status, string? Rejection,
    uint InstrumentToken, string Exchange, string TradingSymbol, DateTime SubmittedAtUtc,
    DateTime PlannedExitUtc, int Quantity, int Lots, DateTime? EntryUtc, decimal? EntryPrice,
    DateTime? ExitUtc, decimal? ExitPrice, PaperExitReason? ExitReason, decimal? InitialRisk,
    decimal GrossPnl, decimal Fees, decimal NetPnl, string? RiskDecisionSha256,
    IReadOnlyList<RiskRejectionCode> RiskRejectionCodes);

public sealed record PaperTradingResult(int SchemaVersion, Guid SessionId, DateTime CreatedAtUtc,
    string StrategyId, decimal InitialCash, decimal EndingCash, decimal RealizedGrossPnl,
    decimal Fees, decimal RealizedNetPnl, int SubmittedOrders, int FilledTrades,
    int RejectedOrders, IReadOnlyList<PaperTradeResult> Trades);

/// <summary>Deterministically simulates long option purchases from already normalized market ticks.</summary>
public static class PaperTradingEngine
{
    public static PaperTradingResult Run(PaperTradingRequest request, DeterministicRiskPolicySettings riskSettings,
        TimeZoneInfo exchangeTimeZone)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(riskSettings);
        ArgumentNullException.ThrowIfNull(exchangeTimeZone); Validate(request);
        var ticks = request.Ticks.OrderBy(item => item.ReceivedAtUtc).ToArray();
        var results = new List<PaperTradeResult>();
        foreach (var order in request.Orders.OrderBy(item => item.SubmittedAtUtc).ThenBy(item => item.RequestId))
        {
            var matching = ticks.Where(tick => tick.InstrumentToken == order.InstrumentToken &&
                tick.Exchange == order.Exchange && tick.TradingSymbol == order.TradingSymbol).ToArray();
            var entryTick = matching.FirstOrDefault(tick => tick.ReceivedAtUtc >= order.SubmittedAtUtc &&
                tick.ReceivedAtUtc <= order.PlannedExitUtc);
            if (entryTick is null || entryTick.ReceivedAtUtc - order.SubmittedAtUtc > TimeSpan.FromSeconds(request.MaximumTickAgeSeconds))
            {
                results.Add(Rejected(order, "No fresh entry tick was available.")); continue;
            }
            var possibleExits = matching.Where(tick => tick.ReceivedAtUtc > entryTick.ReceivedAtUtc &&
                tick.ReceivedAtUtc <= order.PlannedExitUtc).ToArray();
            if (possibleExits.Length == 0)
            {
                results.Add(Rejected(order, "No exit tick was available after entry.")); continue;
            }
            var entryPrice = ApplySlippage(entryTick.BestAsk ?? entryTick.LastPrice,
                request.SlippageBasisPoints, adverseForBuy: true);
            if (order.StopPrice >= entryPrice || order.TargetPrice <= entryPrice)
            {
                results.Add(Rejected(order, "A long paper order requires stop < entry < target.")); continue;
            }
            var triggered = possibleExits.FirstOrDefault(tick => tick.LastPrice <= order.StopPrice ||
                tick.LastPrice >= order.TargetPrice);
            var exitTick = triggered ?? possibleExits[^1];
            if (triggered is null && order.PlannedExitUtc - exitTick.ReceivedAtUtc >
                TimeSpan.FromSeconds(request.MaximumTickAgeSeconds))
            {
                results.Add(Rejected(order, "No fresh tick was available for the planned exit.")); continue;
            }

            var priorFilled = results.Where(item => item.Status == PaperOrderStatus.FilledAndClosed).ToArray();
            var closed = priorFilled.Where(item => item.ExitUtc <= entryTick.ReceivedAtUtc).Select(item =>
                new ClosedRiskTrade(item.RequestId, request.StrategyId,
                    request.Orders.Single(source => source.RequestId == item.RequestId).InstrumentId,
                    item.EntryUtc!.Value, item.ExitUtc!.Value, item.InitialRisk!.Value, item.NetPnl)).ToArray();
            var open = priorFilled.Where(item => item.EntryUtc <= entryTick.ReceivedAtUtc &&
                item.ExitUtc > entryTick.ReceivedAtUtc).Select(item =>
            {
                var source = request.Orders.Single(candidate => candidate.RequestId == item.RequestId);
                return new OpenRiskPosition(item.RequestId, request.StrategyId, source.InstrumentId,
                    item.EntryUtc!.Value, item.InitialRisk!.Value, item.EntryPrice!.Value * item.Quantity,
                    source.CapitalPool);
            }).ToArray();
            var availableCash = request.InitialCash + closed.Sum(item => item.NetPnl) - open.Sum(item => item.CommittedCapital);
            var state = new RiskPortfolioState(decimal.Max(0, availableCash), request.KillSwitchEngaged,
                new HashSet<string>([request.StrategyId], StringComparer.Ordinal), closed, open);
            var risk = DeterministicRiskPolicy.EvaluateAndSize(riskSettings, state,
                new TradeSizingRiskRequest(order.RequestId, request.StrategyId, order.InstrumentId,
                    entryTick.ReceivedAtUtc, order.PlannedExitUtc, entryPrice, order.StopPrice,
                    order.LotSize, order.MaximumLots, order.CapitalPool), exchangeTimeZone);
            if (!risk.Approved)
            {
                results.Add(new(order.RequestId, PaperOrderStatus.RiskRejected, "M18 risk policy rejected the order.",
                    order.InstrumentToken, order.Exchange, order.TradingSymbol, order.SubmittedAtUtc,
                    order.PlannedExitUtc, 0, 0, null, null, null, null, null, null, 0, 0, 0,
                    risk.DecisionSha256, risk.PolicyDecision.RejectionCodes));
                continue;
            }

            var exitReason = exitTick.LastPrice <= order.StopPrice ? PaperExitReason.Stop :
                exitTick.LastPrice >= order.TargetPrice ? PaperExitReason.Target : PaperExitReason.PlannedExit;
            var exitPrice = ApplySlippage(exitTick.BestBid ?? exitTick.LastPrice,
                request.SlippageBasisPoints, adverseForBuy: false);
            var quantity = risk.PositionSize.Quantity;
            var gross = (exitPrice - entryPrice) * quantity;
            var fees = request.FixedFeePerFill * 2m +
                ((entryPrice + exitPrice) * quantity * request.FeeBasisPointsPerSide / 10_000m);
            var net = gross - fees;
            results.Add(new(order.RequestId, PaperOrderStatus.FilledAndClosed, null, order.InstrumentToken,
                order.Exchange, order.TradingSymbol, order.SubmittedAtUtc, order.PlannedExitUtc,
                quantity, risk.PositionSize.Lots, entryTick.ReceivedAtUtc, entryPrice, exitTick.ReceivedAtUtc,
                exitPrice, exitReason, risk.PositionSize.TotalRisk, gross, fees, net,
                risk.DecisionSha256, risk.PolicyDecision.RejectionCodes));
        }
        var filled = results.Where(item => item.Status == PaperOrderStatus.FilledAndClosed).ToArray();
        var grossPnl = filled.Sum(item => item.GrossPnl); var feesTotal = filled.Sum(item => item.Fees);
        var netPnl = filled.Sum(item => item.NetPnl);
        return new(1, request.SessionId, request.CreatedAtUtc, request.StrategyId, request.InitialCash,
            request.InitialCash + netPnl, grossPnl, feesTotal, netPnl, request.Orders.Count,
            filled.Length, results.Count - filled.Length, results.AsReadOnly());
    }

    private static PaperTradeResult Rejected(PaperTradeIntent order, string reason) =>
        new(order.RequestId, PaperOrderStatus.DataRejected, reason, order.InstrumentToken, order.Exchange,
            order.TradingSymbol, order.SubmittedAtUtc, order.PlannedExitUtc, 0, 0, null, null, null, null,
            null, null, 0, 0, 0, null, []);

    private static decimal ApplySlippage(decimal price, decimal basisPoints, bool adverseForBuy) =>
        price * (1m + (adverseForBuy ? basisPoints : -basisPoints) / 10_000m);

    private static void Validate(PaperTradingRequest request)
    {
        if (request.SessionId == Guid.Empty || request.CreatedAtUtc.Kind != DateTimeKind.Utc ||
            string.IsNullOrWhiteSpace(request.StrategyId) || request.StrategyId.Length > 128 ||
            request.InitialCash <= 0 || request.SlippageBasisPoints < 0 || request.SlippageBasisPoints > 1000 ||
            request.FeeBasisPointsPerSide < 0 || request.FeeBasisPointsPerSide > 1000 ||
            request.FixedFeePerFill < 0 || request.MaximumTickAgeSeconds is < 1 or > 300 ||
            request.Orders is null || request.Ticks is null || request.Orders.Count is < 1 or > 1000 ||
            request.Ticks.Count is < 2 or > 100_000)
            throw new ArgumentException("Paper-trading session settings are invalid.", nameof(request));
        if (request.Orders.Select(item => item.RequestId).Distinct().Count() != request.Orders.Count ||
            request.Orders.Any(item => item.RequestId == Guid.Empty || item.InstrumentId == Guid.Empty ||
                item.InstrumentToken == 0 || string.IsNullOrWhiteSpace(item.Exchange) ||
                string.IsNullOrWhiteSpace(item.TradingSymbol) || item.SubmittedAtUtc.Kind != DateTimeKind.Utc ||
                item.PlannedExitUtc.Kind != DateTimeKind.Utc || item.SubmittedAtUtc >= item.PlannedExitUtc ||
                item.StopPrice <= 0 || item.TargetPrice <= 0 || item.LotSize < 1 || item.MaximumLots is <= 0 ||
                !Enum.IsDefined(item.CapitalPool)))
            throw new ArgumentException("Paper-order identity, UTC times, prices or sizing are invalid.", nameof(request));
        if (request.Ticks.Any(item => item.ReceivedAtUtc.Kind != DateTimeKind.Utc || item.LastPrice <= 0) ||
            request.Ticks.Zip(request.Ticks.Skip(1), (left, right) => left.ReceivedAtUtc > right.ReceivedAtUtc).Any(value => value))
            throw new ArgumentException("Paper ticks must be positive, UTC and chronological.", nameof(request));
    }
}
