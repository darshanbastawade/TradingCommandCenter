using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trading.Backtesting.Comparison;

public static class CrossEngineComparisonCodec
{
    private static readonly JsonSerializerOptions Canonical = Options(false);
    private static readonly JsonSerializerOptions Display = Options(true);

    public static CrossEngineComparison Seal(CrossEngineComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        if (comparison.SchemaVersion != 1 || comparison.Trades is null || comparison.Policy is null ||
            comparison.IndependentValidation is not null &&
            !Trading.Application.Backtesting.BacktestRunCodec.VerifyExternalValidation(
                comparison.IndependentValidation))
            throw new ArgumentException("Comparison schema is invalid.", nameof(comparison));
        var unsigned = comparison with { ComparisonSha256 = string.Empty };
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(unsigned, Canonical)))).ToLowerInvariant();
        return unsigned with { ComparisonSha256 = digest };
    }

    public static bool Verify(CrossEngineComparison comparison) => comparison is not null &&
        string.Equals(Seal(comparison).ComparisonSha256, comparison.ComparisonSha256,
            StringComparison.OrdinalIgnoreCase);

    public static string Serialize(CrossEngineComparison comparison) => JsonSerializer.Serialize(comparison, Display);

    private static JsonSerializerOptions Options(bool indented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = indented };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false));
        return options;
    }
}
