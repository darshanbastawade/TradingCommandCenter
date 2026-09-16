using System.Globalization;

namespace Trading.Backtesting.Robustness;

public sealed record ParameterAxis(string Name, IReadOnlyList<decimal> Values);

public static class ParameterNeighborhood
{
    public static IReadOnlyList<ParameterScenario<TParameters>> Cartesian<TParameters>(
        IReadOnlyList<ParameterAxis> axes,
        IReadOnlyDictionary<string, decimal> baseline,
        Func<IReadOnlyDictionary<string, decimal>, TParameters> factory,
        int maximumScenarioCount = 500)
    {
        ArgumentNullException.ThrowIfNull(axes);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(factory);
        if (axes.Count == 0 || maximumScenarioCount < 1 ||
            axes.Any(axis => string.IsNullOrWhiteSpace(axis.Name) || axis.Values is null || axis.Values.Count == 0 ||
                axis.Values.Distinct().Count() != axis.Values.Count) ||
            axes.Select(axis => axis.Name).Distinct(StringComparer.Ordinal).Count() != axes.Count ||
            baseline.Count != axes.Count || axes.Any(axis => !baseline.TryGetValue(axis.Name, out var value) ||
                !axis.Values.Contains(value)))
            throw new ArgumentException("Parameter axes and baseline values are invalid.", nameof(axes));

        long count = 1;
        foreach (var axis in axes)
        {
            count *= axis.Values.Count;
            if (count > maximumScenarioCount || count > 500)
                throw new ArgumentException("The Cartesian parameter neighborhood exceeds its scenario limit.", nameof(axes));
        }

        var scenarios = new List<ParameterScenario<TParameters>>((int)count);
        Build(0, new Dictionary<string, decimal>(StringComparer.Ordinal));
        return scenarios.AsReadOnly();

        void Build(int axisIndex, Dictionary<string, decimal> values)
        {
            if (axisIndex == axes.Count)
            {
                var frozen = new Dictionary<string, decimal>(values, StringComparer.Ordinal);
                var isBaseline = axes.All(axis => frozen[axis.Name] == baseline[axis.Name]);
                var id = string.Join("__", axes.Select(axis =>
                    $"{axis.Name}={frozen[axis.Name].ToString(CultureInfo.InvariantCulture)}"));
                scenarios.Add(new(id, factory(frozen), frozen, isBaseline));
                return;
            }
            var axis = axes[axisIndex];
            foreach (var value in axis.Values)
            {
                values[axis.Name] = value;
                Build(axisIndex + 1, values);
            }
            values.Remove(axis.Name);
        }
    }
}
