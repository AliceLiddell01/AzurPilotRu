namespace AzurPilot.Core.Capture;

/// <summary>Робастная сводка выборки длительностей в миллисекундах.</summary>
/// <param name="Count">Число значений.</param>
/// <param name="Minimum">Минимум.</param>
/// <param name="Median">p50.</param>
/// <param name="P95">p95.</param>
/// <param name="P99">p99.</param>
/// <param name="Maximum">Максимум.</param>
/// <param name="Mean">Среднее.</param>
/// <param name="StandardDeviation">Стандартное отклонение по всей совокупности.</param>
public sealed record SampleStatistics(int Count, double Minimum, double Median, double P95, double P99,
    double Maximum, double Mean, double StandardDeviation)
{
    /// <summary>Пустая сводка.</summary>
    public static SampleStatistics Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>Строит сводку; нечисловые и отрицательные значения отвергаются.</summary>
    /// <exception cref="ArgumentException">Значение не конечно или отрицательно.</exception>
    public static SampleStatistics From(IReadOnlyCollection<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return Empty;
        }

        double[] ordered = [.. values];
        foreach (double value in ordered)
        {
            if (!double.IsFinite(value) || value < 0)
            {
                throw new ArgumentException("Длительность должна быть конечной и неотрицательной.", nameof(values));
            }
        }

        Array.Sort(ordered);
        double mean = ordered.Average();
        double variance = ordered.Sum(value => (value - mean) * (value - mean)) / ordered.Length;
        return new SampleStatistics(ordered.Length, ordered[0], Percentile(ordered, 0.50), Percentile(ordered, 0.95),
            Percentile(ordered, 0.99), ordered[^1], mean, Math.Sqrt(variance));
    }

    /// <summary>Перцентиль с линейной интерполяцией по упорядоченной выборке (как в исходном benchmark).</summary>
    public static double Percentile(IReadOnlyList<double> ordered, double fraction)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        ArgumentOutOfRangeException.ThrowIfLessThan(fraction, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fraction, 1);
        if (ordered.Count == 0)
        {
            return 0;
        }

        double position = (ordered.Count - 1) * fraction;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        return lower == upper
            ? ordered[lower]
            : ordered[lower] + ((ordered[upper] - ordered[lower]) * (position - lower));
    }
}
