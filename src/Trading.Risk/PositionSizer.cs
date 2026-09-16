namespace Trading.Risk;

public sealed record PositionSizeRequest(
    decimal AllowedRisk,
    decimal EntryPrice,
    decimal StopPrice,
    int LotSize,
    decimal MaximumCapital,
    int? MaximumLots = null);

public sealed record PositionSizeResult(
    int Quantity,
    int Lots,
    decimal RiskPerUnit,
    decimal TotalRisk,
    decimal CapitalRequired,
    decimal UnusedRisk)
{
    public bool CanTrade => Quantity > 0;
}

public static class PositionSizer
{
    public static PositionSizeResult Calculate(PositionSizeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.AllowedRisk <= 0) throw new ArgumentOutOfRangeException(nameof(request), "Allowed risk must be positive.");
        if (request.EntryPrice <= 0 || request.StopPrice <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Entry and stop prices must be positive.");
        if (request.LotSize < 1) throw new ArgumentOutOfRangeException(nameof(request), "Lot size must be positive.");
        if (request.MaximumCapital <= 0) throw new ArgumentOutOfRangeException(nameof(request), "Maximum capital must be positive.");
        if (request.MaximumLots is <= 0) throw new ArgumentOutOfRangeException(nameof(request), "Maximum lots must be positive when supplied.");

        var riskPerUnit = decimal.Abs(request.EntryPrice - request.StopPrice);
        if (riskPerUnit == 0) throw new ArgumentException("Entry and stop prices must differ.", nameof(request));

        var unitsByRisk = decimal.Floor(request.AllowedRisk / riskPerUnit);
        var unitsByCapital = decimal.Floor(request.MaximumCapital / request.EntryPrice);
        var rawUnits = decimal.Min(unitsByRisk, unitsByCapital);
        var lots = decimal.Floor(rawUnits / request.LotSize);
        if (request.MaximumLots is not null) lots = decimal.Min(lots, request.MaximumLots.Value);
        if (lots > int.MaxValue / request.LotSize)
            throw new ArgumentOutOfRangeException(nameof(request), "Calculated quantity exceeds the supported range.");

        var quantity = (int)lots * request.LotSize;
        var totalRisk = quantity * riskPerUnit;
        return new(quantity, (int)lots, riskPerUnit, totalRisk, quantity * request.EntryPrice,
            request.AllowedRisk - totalRisk);
    }
}
