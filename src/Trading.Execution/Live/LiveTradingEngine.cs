using Trading.Application.Execution;
using Trading.Risk.Policy;

namespace Trading.Execution.Live;

public enum LiveTradingMode { SemiLive = 1, DirectLive = 2 }

public sealed record LiveTradingSettings
{
    public bool KillSwitchEngaged { get; init; } = true;
    public bool AllowDirectOrders { get; init; }
    public int MaximumBrokerDataAgeSeconds { get; init; } = 5;
    public decimal MaximumLimitPremiumBasisPoints { get; init; } = 25m;
    public int MinimumPaperSessions { get; init; } = 10;
    public int MinimumPaperFilledTrades { get; init; } = 30;
    public bool RequirePositivePaperNetPnl { get; init; } = true;
}

public sealed record LiveEntryIntent(Guid RequestId, Guid InstrumentId, uint InstrumentToken,
    string Exchange, string TradingSymbol, DateTime ProposedEntryUtc, DateTime PlannedExitUtc,
    decimal LimitPrice, decimal StopPrice, decimal TargetPrice, int LotSize, int? MaximumLots,
    bool OperatorApproved);

public sealed record LiveOrderProposal(Guid RequestId, string StrategyId, uint InstrumentToken,
    string Exchange, string TradingSymbol, DateTime EvaluatedAtUtc, decimal QuoteLastPrice,
    decimal QuoteBestAsk, decimal LimitPrice, decimal StopPrice, decimal TargetPrice,
    int Quantity, int Lots, decimal InitialRisk, decimal RequiredCapital,
    string RiskDecisionSha256);

public static class LiveTradingEngine
{
    public static LiveOrderProposal Prepare(DeterministicRiskPolicySettings riskSettings,
        LiveTradingSettings liveSettings, BrokerAccountSnapshot account, BrokerQuote quote,
        LiveEntryIntent intent, string strategyId, DateTime evaluatedAtUtc, TimeZoneInfo exchangeTimeZone)
    {
        ArgumentNullException.ThrowIfNull(riskSettings); ArgumentNullException.ThrowIfNull(liveSettings);
        ArgumentNullException.ThrowIfNull(account); ArgumentNullException.ThrowIfNull(quote);
        ArgumentNullException.ThrowIfNull(intent); ArgumentNullException.ThrowIfNull(exchangeTimeZone);
        if (evaluatedAtUtc.Kind != DateTimeKind.Utc || intent.RequestId == Guid.Empty ||
            intent.InstrumentId == Guid.Empty || intent.InstrumentToken == 0 || string.IsNullOrWhiteSpace(strategyId) ||
            string.IsNullOrWhiteSpace(intent.Exchange) || string.IsNullOrWhiteSpace(intent.TradingSymbol) ||
            intent.ProposedEntryUtc.Kind != DateTimeKind.Utc || intent.PlannedExitUtc.Kind != DateTimeKind.Utc ||
            intent.LimitPrice <= 0 || intent.StopPrice <= 0 || intent.TargetPrice <= 0 ||
            intent.StopPrice >= intent.LimitPrice || intent.TargetPrice <= intent.LimitPrice ||
            intent.LotSize < 1 || intent.MaximumLots is <= 0)
            throw new ArgumentException("The live entry intent is invalid.", nameof(intent));
        if (liveSettings.MaximumBrokerDataAgeSeconds is < 1 or > 30 ||
            liveSettings.MaximumLimitPremiumBasisPoints is < 0 or > 1000 ||
            liveSettings.MinimumPaperSessions < 1 || liveSettings.MinimumPaperFilledTrades < 1)
            throw new ArgumentException("Live trading settings are invalid.", nameof(liveSettings));
        if (account.AsOfUtc.Kind != DateTimeKind.Utc || quote.AsOfUtc.Kind != DateTimeKind.Utc ||
            account.AsOfUtc > evaluatedAtUtc || quote.AsOfUtc > evaluatedAtUtc ||
            evaluatedAtUtc - account.AsOfUtc > TimeSpan.FromSeconds(liveSettings.MaximumBrokerDataAgeSeconds) ||
            evaluatedAtUtc - quote.AsOfUtc > TimeSpan.FromSeconds(liveSettings.MaximumBrokerDataAgeSeconds))
            throw new InvalidOperationException("Broker account or quote data is stale.");
        if (intent.ProposedEntryUtc > evaluatedAtUtc || evaluatedAtUtc - intent.ProposedEntryUtc >
            TimeSpan.FromSeconds(liveSettings.MaximumBrokerDataAgeSeconds))
            throw new InvalidOperationException("The proposed entry timestamp is stale.");
        if (account.AvailableCash <= 0) throw new InvalidOperationException("Broker available cash is not positive.");
        if (account.Positions.Any(item => item.Quantity != 0))
            throw new InvalidOperationException("M23 requires a flat broker account before a live entry.");
        if (account.DayOrderCount != 0)
            throw new InvalidOperationException("M23 refuses live entry after any broker order activity on the same day.");
        if (quote.InstrumentToken != intent.InstrumentToken || quote.Exchange != intent.Exchange ||
            quote.TradingSymbol != intent.TradingSymbol || quote.LastPrice <= 0 || quote.BestAsk <= 0 ||
            quote.BestBid <= 0 || quote.BestAsk < quote.BestBid)
            throw new InvalidOperationException("The broker quote identity or prices are invalid.");
        var maximumLimit = quote.BestAsk * (1m + liveSettings.MaximumLimitPremiumBasisPoints / 10_000m);
        if (intent.LimitPrice < quote.BestAsk || intent.LimitPrice > maximumLimit)
            throw new InvalidOperationException("The buy limit must be marketable and within the configured price guard.");

        var state = new RiskPortfolioState(account.AvailableCash, liveSettings.KillSwitchEngaged,
            new HashSet<string>([strategyId], StringComparer.Ordinal), [], []);
        var risk = DeterministicRiskPolicy.EvaluateAndSize(riskSettings, state,
            new(intent.RequestId, strategyId, intent.InstrumentId, intent.ProposedEntryUtc,
                intent.PlannedExitUtc, intent.LimitPrice, intent.StopPrice, intent.LotSize,
                intent.MaximumLots, TradingCapitalPool.StrategyTesting), exchangeTimeZone);
        if (!risk.Approved)
            throw new InvalidOperationException($"M18 rejected the live entry: {string.Join(',', risk.PolicyDecision.RejectionCodes)}.");
        return new(intent.RequestId, strategyId, intent.InstrumentToken, intent.Exchange,
            intent.TradingSymbol, evaluatedAtUtc, quote.LastPrice, quote.BestAsk, intent.LimitPrice,
            intent.StopPrice, intent.TargetPrice, risk.PositionSize.Quantity, risk.PositionSize.Lots,
            risk.PositionSize.TotalRisk, risk.PositionSize.CapitalRequired, risk.DecisionSha256);
    }
}
