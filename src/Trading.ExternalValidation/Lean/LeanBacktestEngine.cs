using Trading.Application.Backtesting;
using Trading.Application.MarketData;
using Trading.Domain.MarketData;

namespace Trading.ExternalValidation.Lean;

public sealed class LeanBacktestEngine(IMarketDataStore marketData, ILeanProcessRunner runner,
    LeanOptions options) : IBacktestEngine
{
    public const string Id = "lean";
    public const string AdapterVersion = "2";
    public string EngineId => Id;
    public string EngineVersion => $"{AdapterVersion}:{options.Image}";
    public BacktestEngineRole Role => BacktestEngineRole.IndependentValidation;

    public async Task<BacktestRun> RunAsync(SealedBacktestSpecification sealedSpecification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sealedSpecification);
        if (!BacktestSpecificationCodec.Verify(sealedSpecification))
            throw new ArgumentException("The sealed backtest specification hash is invalid.",
                nameof(sealedSpecification));
        var specification = BacktestSpecificationCodec.NormalizeAndValidate(sealedSpecification.Specification);
        if (specification.Data.MarketDataMode != BacktestMarketDataMode.OhlcvBars ||
            specification.Instrument.AssetClass == BacktestAssetClass.IndexOption)
            throw new NotSupportedException("LEAN adapter v1 supports OHLCV equity and equity-index data only.");
        LeanStrategyTranslator.Validate(specification);
        if (string.IsNullOrWhiteSpace(options.DataDirectory) || !Directory.Exists(options.DataDirectory))
            throw new InvalidOperationException("LEAN data directory is not configured or does not exist.");
        var instrument = await marketData.FindInstrumentAsync(specification.Instrument.InstrumentId,
            cancellationToken) ?? throw new ArgumentException("The specification instrument is not registered.");
        if (!string.Equals(instrument.Exchange, specification.Instrument.Exchange,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(instrument.Symbol, specification.Instrument.TradingSymbol,
                StringComparison.OrdinalIgnoreCase) ||
            instrument.LotSize != specification.Instrument.LotSize ||
            instrument.TickSize != specification.Instrument.TickSize)
            throw new InvalidOperationException("Registered instrument metadata does not match the specification.");

        var candles = await ReadAllAsync(instrument.Id,
            (Timeframe)specification.Data.TimeframeMinutes, specification.Data.FromUtc,
            specification.Data.ToUtc, cancellationToken);
        var project = LeanAlgorithmProject.Verify(options.ProjectDirectory, specification.StrategyId);
        var mapped = await LeanInputMapper.WriteAsync(options.DataDirectory,
            sealedSpecification, candles, project, options.Image, cancellationToken);
        try
        {
            var output = await runner.RunAsync(mapped, cancellationToken);
            return LeanResultMapper.Read(output, mapped, sealedSpecification, EngineVersion);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(mapped.RequestPath)!, true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private async Task<IReadOnlyList<Candle>> ReadAllAsync(Guid instrumentId, Timeframe timeframe,
        DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
    {
        var result = new List<Candle>();
        var cursor = new DateTimeOffset(fromUtc);
        var end = new DateTimeOffset(toUtc);
        while (cursor < end)
        {
            var page = await marketData.ReadCandlesAsync(instrumentId, timeframe, cursor, end,
                10_000, cancellationToken);
            result.AddRange(page);
            if (page.Count < 10_000) break;
            cursor = new DateTimeOffset(page[^1].OpenTimeUtc.AddTicks(1));
        }
        return result.AsReadOnly();
    }
}
