using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Trading.Domain.MarketData;

namespace Trading.Risk.Policy;

public enum TradingCapitalPool { Active = 1, StrategyTesting = 2 }

public enum RiskRejectionCode
{
    KillSwitchEngaged = 1,
    StrategyNotQualified = 2,
    OutsideTradingSession = 3,
    OvernightPositionForbidden = 4,
    MaximumOpenPositionsReached = 5,
    AveragingForbidden = 6,
    DailyTradeLimitReached = 7,
    ConsecutiveLossLimitReached = 8,
    DailyLossLimitReached = 9,
    WeeklyLossLimitReached = 10,
    MonthlyLossLimitReached = 11,
    ProposedRiskExceedsAvailableRisk = 12,
    CapitalPoolLimitExceeded = 13,
    AvailableCashExceeded = 14,
    AggregateOpenRiskExceeded = 15,
    ExistingOvernightPosition = 16
}

public sealed record CapitalAllocation(
    decimal TotalCapital = 300_000m,
    decimal ProtectedReserve = 150_000m,
    decimal ActiveTradingCapital = 100_000m,
    decimal StrategyTestingCapital = 30_000m,
    decimal CashBuffer = 20_000m);

public sealed record DeterministicRiskPolicySettings
{
    public string PolicyId { get; init; } = "india-intraday-options-conservative-v1";
    public CapitalAllocation Capital { get; init; } = new();
    public decimal MaximumRiskPerTrade { get; init; } = 750m;
    public decimal MaximumDailyLoss { get; init; } = 1_500m;
    public decimal MaximumWeeklyLoss { get; init; } = 3_000m;
    public decimal MaximumMonthlyLoss { get; init; } = 6_000m;
    public decimal MaximumAggregateOpenRisk { get; init; } = 750m;
    public int MaximumTradesPerDay { get; init; } = 3;
    public int MaximumConsecutiveLossesPerDay { get; init; } = 2;
    public int MaximumOpenPositions { get; init; } = 1;
    public TimeOnly EntryWindowStart { get; init; } = new(9, 15);
    public TimeOnly LastEntryTime { get; init; } = new(15, 0);
    public TimeOnly MandatoryExitTime { get; init; } = new(15, 25);
    public bool RequireQualifiedStrategy { get; init; } = true;
}

public sealed record ClosedRiskTrade(Guid TradeId, string StrategyId, Guid InstrumentId,
    DateTime EntryUtc, DateTime ExitUtc, decimal InitialRisk, decimal NetPnl);

public sealed record OpenRiskPosition(Guid PositionId, string StrategyId, Guid InstrumentId,
    DateTime EntryUtc, decimal InitialRisk, decimal CommittedCapital, TradingCapitalPool CapitalPool);

public sealed record RiskPortfolioState(decimal AvailableCash, bool KillSwitchEngaged,
    IReadOnlySet<string> QualifiedStrategyIds, IReadOnlyList<ClosedRiskTrade> ClosedTrades,
    IReadOnlyList<OpenRiskPosition> OpenPositions);

public sealed record TradeRiskRequest(Guid RequestId, string StrategyId, Guid InstrumentId,
    DateTime ProposedEntryUtc, DateTime PlannedExitUtc, decimal ProposedRisk,
    decimal RequiredCapital, TradingCapitalPool CapitalPool, bool IsScaleIn = false);

public sealed record TradeSizingRiskRequest(Guid RequestId, string StrategyId, Guid InstrumentId,
    DateTime ProposedEntryUtc, DateTime PlannedExitUtc, decimal EntryPrice, decimal StopPrice,
    int LotSize, int? MaximumLots, TradingCapitalPool CapitalPool, bool IsScaleIn = false);

public sealed record RiskPeriodState(decimal RealizedPnl, decimal RemainingLossCapacity);

public sealed record RiskDecision(
    int SchemaVersion,
    string PolicyId,
    string CalendarId,
    string CalendarSha256,
    Guid RequestId,
    bool Approved,
    decimal MaximumApprovedRisk,
    decimal MaximumApprovedCapital,
    int TradesToday,
    int ConsecutiveLossesToday,
    RiskPeriodState Day,
    RiskPeriodState Week,
    RiskPeriodState Month,
    IReadOnlyList<RiskRejectionCode> RejectionCodes,
    string DecisionSha256);

public sealed record RiskSizedDecision(RiskDecision PolicyDecision, PositionSizeResult PositionSize,
    bool CanFundWholeLot, bool Approved, string DecisionSha256);

/// <summary>A pure pre-trade gate. Identical settings, state and request produce an identical decision.</summary>
public static class DeterministicRiskPolicy
{
    public static RiskSizedDecision EvaluateAndSize(DeterministicRiskPolicySettings settings,
        RiskPortfolioState state, TradeSizingRiskRequest request, ExchangeSessionCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.EntryPrice <= 0 || request.StopPrice <= 0 || request.EntryPrice == request.StopPrice ||
            request.LotSize < 1 || request.MaximumLots is <= 0)
            throw new ArgumentException("Trade sizing inputs are invalid.", nameof(request));
        var probeRequest = new TradeRiskRequest(request.RequestId, request.StrategyId, request.InstrumentId,
            request.ProposedEntryUtc, request.PlannedExitUtc, .0001m, .0001m, request.CapitalPool, request.IsScaleIn);
        var probe = Evaluate(settings, state, probeRequest, calendar);
        var riskPerUnit = decimal.Abs(request.EntryPrice - request.StopPrice);
        PositionSizeResult size;
        if (probe.MaximumApprovedRisk <= 0 || probe.MaximumApprovedCapital <= 0)
            size = new(0, 0, riskPerUnit, 0, 0, probe.MaximumApprovedRisk);
        else
            size = PositionSizer.Calculate(new(probe.MaximumApprovedRisk, request.EntryPrice, request.StopPrice,
                request.LotSize, probe.MaximumApprovedCapital, request.MaximumLots));
        var decision = size.CanTrade
            ? Evaluate(settings, state, new(request.RequestId, request.StrategyId, request.InstrumentId,
                request.ProposedEntryUtc, request.PlannedExitUtc, size.TotalRisk, size.CapitalRequired,
                request.CapitalPool, request.IsScaleIn), calendar)
            : probe;
        var hashInput = string.Join('|', decision.DecisionSha256, I(request.EntryPrice), I(request.StopPrice),
            request.LotSize.ToString(CultureInfo.InvariantCulture), request.MaximumLots?.ToString(CultureInfo.InvariantCulture) ?? "null",
            size.Quantity.ToString(CultureInfo.InvariantCulture), I(size.TotalRisk), I(size.CapitalRequired));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput))).ToLowerInvariant();
        return new(decision, size, size.CanTrade, decision.Approved && size.CanTrade, hash);
    }

    public static RiskDecision Evaluate(DeterministicRiskPolicySettings settings, RiskPortfolioState state,
        TradeRiskRequest request, ExchangeSessionCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(calendar);
        Validate(settings, state, request);

        var entryDate = calendar.LocalDate(request.ProposedEntryUtc);
        var exitDate = calendar.LocalDate(request.PlannedExitUtc);
        var localEntry = calendar.LocalTime(request.ProposedEntryUtc);
        var localExit = calendar.LocalTime(request.PlannedExitUtc);
        var hasEntrySession = calendar.TryGetSession(entryDate, out var exchangeSession);
        var todayTrades = state.ClosedTrades.Where(trade => calendar.LocalDate(trade.EntryUtc) == entryDate)
            .OrderBy(trade => trade.ExitUtc).ToArray();
        var consecutiveLosses = todayTrades.Reverse().TakeWhile(trade => trade.NetPnl < 0).Count();
        var weekStart = entryDate.AddDays(-(((int)entryDate.DayOfWeek + 6) % 7));
        var monthStart = new DateOnly(entryDate.Year, entryDate.Month, 1);
        var dayPnl = Realized(state.ClosedTrades, entryDate, entryDate.AddDays(1), calendar);
        var weekPnl = Realized(state.ClosedTrades, weekStart, weekStart.AddDays(7), calendar);
        var monthPnl = Realized(state.ClosedTrades, monthStart, monthStart.AddMonths(1), calendar);
        var day = Period(dayPnl, settings.MaximumDailyLoss);
        var week = Period(weekPnl, settings.MaximumWeeklyLoss);
        var month = Period(monthPnl, settings.MaximumMonthlyLoss);
        var maximumRisk = decimal.Max(0, new[] { settings.MaximumRiskPerTrade, day.RemainingLossCapacity,
            week.RemainingLossCapacity, month.RemainingLossCapacity,
            settings.MaximumAggregateOpenRisk - state.OpenPositions.Sum(position => position.InitialRisk) }.Min());
        var poolCapital = request.CapitalPool == TradingCapitalPool.Active
            ? settings.Capital.ActiveTradingCapital : settings.Capital.StrategyTestingCapital;
        var poolCommittedCapital = state.OpenPositions.Where(position => position.CapitalPool == request.CapitalPool)
            .Sum(position => position.CommittedCapital);
        var maximumCapital = decimal.Max(0, decimal.Min(poolCapital - poolCommittedCapital,
            state.AvailableCash));
        var reasons = new List<RiskRejectionCode>();

        Add(state.KillSwitchEngaged, RiskRejectionCode.KillSwitchEngaged);
        Add(settings.RequireQualifiedStrategy && !state.QualifiedStrategyIds.Contains(request.StrategyId),
            RiskRejectionCode.StrategyNotQualified);
        Add(!hasEntrySession || localEntry < settings.EntryWindowStart ||
            (hasEntrySession && localEntry < exchangeSession.Open) ||
            localEntry >= settings.LastEntryTime ||
            (hasEntrySession && localEntry >= exchangeSession.Close),
            RiskRejectionCode.OutsideTradingSession);
        Add(!hasEntrySession || exitDate != entryDate || request.PlannedExitUtc <= request.ProposedEntryUtc ||
            localExit > settings.MandatoryExitTime ||
            (hasEntrySession && localExit > exchangeSession.Close), RiskRejectionCode.OvernightPositionForbidden);
        Add(state.OpenPositions.Any(position => calendar.LocalDate(position.EntryUtc) != entryDate),
            RiskRejectionCode.ExistingOvernightPosition);
        Add(state.OpenPositions.Count >= settings.MaximumOpenPositions,
            RiskRejectionCode.MaximumOpenPositionsReached);
        Add(request.IsScaleIn || state.OpenPositions.Any(position => position.InstrumentId == request.InstrumentId),
            RiskRejectionCode.AveragingForbidden);
        Add(todayTrades.Length >= settings.MaximumTradesPerDay, RiskRejectionCode.DailyTradeLimitReached);
        Add(consecutiveLosses >= settings.MaximumConsecutiveLossesPerDay,
            RiskRejectionCode.ConsecutiveLossLimitReached);
        Add(day.RemainingLossCapacity <= 0, RiskRejectionCode.DailyLossLimitReached);
        Add(week.RemainingLossCapacity <= 0, RiskRejectionCode.WeeklyLossLimitReached);
        Add(month.RemainingLossCapacity <= 0, RiskRejectionCode.MonthlyLossLimitReached);
        Add(request.ProposedRisk > maximumRisk, RiskRejectionCode.ProposedRiskExceedsAvailableRisk);
        Add(request.RequiredCapital > poolCapital - poolCommittedCapital, RiskRejectionCode.CapitalPoolLimitExceeded);
        Add(request.RequiredCapital > state.AvailableCash, RiskRejectionCode.AvailableCashExceeded);
        Add(state.OpenPositions.Sum(position => position.InitialRisk) + request.ProposedRisk >
            settings.MaximumAggregateOpenRisk, RiskRejectionCode.AggregateOpenRiskExceeded);

        var distinct = reasons.Distinct().Order().ToArray();
        var hash = Hash(settings, state, request, calendar, maximumRisk, maximumCapital, distinct);
        return new(2, settings.PolicyId, calendar.Id, calendar.Sha256, request.RequestId, distinct.Length == 0, maximumRisk,
            maximumCapital, todayTrades.Length, consecutiveLosses, day, week, month,
            Array.AsReadOnly(distinct), hash);

        void Add(bool condition, RiskRejectionCode code) { if (condition) reasons.Add(code); }
    }

    private static RiskPeriodState Period(decimal pnl, decimal limit) =>
        new(pnl, decimal.Max(0, limit + pnl));

    private static decimal Realized(IEnumerable<ClosedRiskTrade> trades, DateOnly from, DateOnly to,
        ExchangeSessionCalendar calendar) => trades.Where(trade =>
        {
            var session = calendar.LocalDate(trade.ExitUtc);
            return session >= from && session < to;
        }).Sum(trade => trade.NetPnl);

    private static void Validate(DeterministicRiskPolicySettings settings, RiskPortfolioState state,
        TradeRiskRequest request)
    {
        var capital = settings.Capital;
        if (string.IsNullOrWhiteSpace(settings.PolicyId) || capital.TotalCapital <= 0 ||
            new[] { capital.ProtectedReserve, capital.ActiveTradingCapital, capital.StrategyTestingCapital,
                capital.CashBuffer }.Any(value => value < 0) ||
            capital.ProtectedReserve + capital.ActiveTradingCapital + capital.StrategyTestingCapital + capital.CashBuffer != capital.TotalCapital)
            throw new ArgumentException("Capital allocation must be non-negative and total exactly.", nameof(settings));
        if (new[] { settings.MaximumRiskPerTrade, settings.MaximumDailyLoss, settings.MaximumWeeklyLoss,
                settings.MaximumMonthlyLoss, settings.MaximumAggregateOpenRisk }.Any(value => value <= 0) ||
            settings.MaximumDailyLoss > settings.MaximumWeeklyLoss || settings.MaximumWeeklyLoss > settings.MaximumMonthlyLoss ||
            settings.MaximumTradesPerDay < 1 || settings.MaximumConsecutiveLossesPerDay < 1 ||
            settings.MaximumOpenPositions < 1 || settings.EntryWindowStart >= settings.LastEntryTime ||
            settings.LastEntryTime >= settings.MandatoryExitTime)
            throw new ArgumentException("Risk limits are invalid.", nameof(settings));
        if (state.AvailableCash < 0 || state.QualifiedStrategyIds is null || state.ClosedTrades is null || state.OpenPositions is null)
            throw new ArgumentException("Portfolio state is invalid.", nameof(state));
        if (request.RequestId == Guid.Empty || request.InstrumentId == Guid.Empty || string.IsNullOrWhiteSpace(request.StrategyId) ||
            request.ProposedEntryUtc.Kind != DateTimeKind.Utc || request.PlannedExitUtc.Kind != DateTimeKind.Utc ||
            request.ProposedRisk <= 0 || request.RequiredCapital <= 0 || !Enum.IsDefined(request.CapitalPool))
            throw new ArgumentException("Trade risk request is invalid.", nameof(request));
        if (state.ClosedTrades.Any(trade => trade.TradeId == Guid.Empty || trade.InstrumentId == Guid.Empty ||
                string.IsNullOrWhiteSpace(trade.StrategyId) || trade.EntryUtc >= trade.ExitUtc ||
                trade.EntryUtc.Kind != DateTimeKind.Utc || trade.ExitUtc.Kind != DateTimeKind.Utc ||
                trade.ExitUtc > request.ProposedEntryUtc || trade.InitialRisk <= 0) ||
            state.ClosedTrades.Select(trade => trade.TradeId).Distinct().Count() != state.ClosedTrades.Count)
            throw new ArgumentException("Closed trade history is invalid.", nameof(state));
        if (state.OpenPositions.Any(position => position.PositionId == Guid.Empty || position.InstrumentId == Guid.Empty ||
                string.IsNullOrWhiteSpace(position.StrategyId) || position.InitialRisk <= 0 || position.CommittedCapital <= 0) ||
            state.OpenPositions.Any(position => position.EntryUtc.Kind != DateTimeKind.Utc ||
                position.EntryUtc > request.ProposedEntryUtc || !Enum.IsDefined(position.CapitalPool)) ||
            state.OpenPositions.Select(position => position.PositionId).Distinct().Count() != state.OpenPositions.Count)
            throw new ArgumentException("Open position state is invalid.", nameof(state));
    }

    private static string Hash(DeterministicRiskPolicySettings settings, RiskPortfolioState state,
        TradeRiskRequest request, ExchangeSessionCalendar calendar, decimal maximumRisk, decimal maximumCapital,
        IReadOnlyList<RiskRejectionCode> reasons)
    {
        var capital = settings.Capital;
        var text = new StringBuilder().Append(settings.PolicyId).Append('|').Append(calendar.Id).Append('|')
            .Append(calendar.Sha256).Append('|').Append(calendar.TimeZone.Id).Append('|')
            .Append(I(capital.TotalCapital)).Append('|').Append(I(capital.ProtectedReserve)).Append('|')
            .Append(I(capital.ActiveTradingCapital)).Append('|').Append(I(capital.StrategyTestingCapital)).Append('|')
            .Append(I(capital.CashBuffer)).Append('|').Append(I(settings.MaximumRiskPerTrade)).Append('|')
            .Append(I(settings.MaximumDailyLoss)).Append('|').Append(I(settings.MaximumWeeklyLoss)).Append('|')
            .Append(I(settings.MaximumMonthlyLoss)).Append('|').Append(I(settings.MaximumAggregateOpenRisk)).Append('|')
            .Append(settings.MaximumTradesPerDay).Append('|').Append(settings.MaximumConsecutiveLossesPerDay).Append('|')
            .Append(settings.MaximumOpenPositions).Append('|').Append(T(settings.EntryWindowStart)).Append('|')
            .Append(T(settings.LastEntryTime)).Append('|').Append(T(settings.MandatoryExitTime)).Append('|')
            .Append(settings.RequireQualifiedStrategy).Append('|').Append(request.RequestId).Append('|')
            .Append(request.StrategyId).Append('|').Append(request.InstrumentId).Append('|')
            .Append(request.ProposedEntryUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append('|')
            .Append(request.PlannedExitUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append('|')
            .Append(I(request.ProposedRisk)).Append('|').Append(I(request.RequiredCapital)).Append('|')
            .Append((int)request.CapitalPool).Append('|').Append(request.IsScaleIn).Append('|')
            .Append(I(state.AvailableCash)).Append('|').Append(state.KillSwitchEngaged).Append('|')
            .Append(I(maximumRisk)).Append('|').Append(I(maximumCapital)).AppendLine();
        foreach (var id in state.QualifiedStrategyIds.Order(StringComparer.Ordinal)) text.Append("Q|").Append(id).AppendLine();
        foreach (var trade in state.ClosedTrades.OrderBy(trade => trade.TradeId))
            text.Append("C|").Append(trade.TradeId).Append('|').Append(trade.StrategyId).Append('|').Append(trade.InstrumentId)
                .Append('|').Append(trade.EntryUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append('|')
                .Append(trade.ExitUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append('|')
                .Append(I(trade.InitialRisk)).Append('|').Append(I(trade.NetPnl)).AppendLine();
        foreach (var position in state.OpenPositions.OrderBy(position => position.PositionId))
            text.Append("O|").Append(position.PositionId).Append('|').Append(position.StrategyId).Append('|')
                .Append(position.InstrumentId).Append('|').Append(position.EntryUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
                .Append('|').Append(I(position.InitialRisk)).Append('|').Append(I(position.CommittedCapital)).Append('|')
                .Append((int)position.CapitalPool).AppendLine();
        foreach (var reason in reasons) text.Append("R|").Append((int)reason).AppendLine();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    private static string I(decimal value) => value.ToString(CultureInfo.InvariantCulture);
    private static string T(TimeOnly value) => value.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
}
