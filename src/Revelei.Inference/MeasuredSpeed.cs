using System.Globalization;

namespace Revelei.Inference;

/// <summary>How fast a model ran on this PC, from its own recent runs (PB76).</summary>
/// <param name="Model">The model's folder name.</param>
/// <param name="Variant">The variant or quantisation it ran.</param>
/// <param name="Provider">Where it ran, e.g. DirectML or Cpu.</param>
/// <param name="SecondsPerMegapixel">The median over its recent runs.</param>
/// <param name="Runs">How many runs the median is over.</param>
/// <param name="At">When the latest of them finished.</param>
public sealed record Measured(string Model, string Variant, string Provider, double SecondsPerMegapixel, int Runs, DateTimeOffset At);

/// <summary>
/// PB76: this PC's own speed per model, read from the operation log every run already writes
/// (PB22), so a measured time replaces the hardware estimate (PB72) once a model has run here.
/// The median of the last few successful runs per model, variant and provider, by megapixel of
/// input, is what a card shows: a single slow first run, which builds an engine, does not set it.
/// </summary>
public static class MeasuredSpeed
{
    /// <summary>How many recent runs the median is over.</summary>
    public const int Window = 5;

    /// <summary>Every model, variant and provider that has run here successfully.</summary>
    /// <param name="records">The log's records, in any order.</param>
    /// <returns>One measurement each.</returns>
    public static IReadOnlyList<Measured> From(IEnumerable<OperationRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        return [.. records
            .Where(r => r.Outcome == "ok" && r.Width > 0 && r.Height > 0 && r.Milliseconds > 0)
            .GroupBy(r => (r.Model, r.Variant, r.Provider))
            .Select(runs =>
            {
                OperationRecord[] recent = [.. runs.OrderByDescending(r => r.At).Take(Window)];
                double[] perMegapixel = [.. recent.Select(r => r.Milliseconds / 1000 / (r.Width * (double)r.Height / 1_000_000)).Order()];
                double median = perMegapixel.Length % 2 == 1
                    ? perMegapixel[perMegapixel.Length / 2]
                    : (perMegapixel[(perMegapixel.Length / 2) - 1] + perMegapixel[perMegapixel.Length / 2]) / 2;
                return new Measured(runs.Key.Model, runs.Key.Variant, runs.Key.Provider, median, recent.Length, recent[0].At);
            })];
    }

    /// <summary>The measurement a model's card shows: its most recent provider's, for the variant when one is named.</summary>
    /// <param name="measured">What <see cref="From"/> found.</param>
    /// <param name="model">The model.</param>
    /// <param name="variant">The variant, or null for any.</param>
    /// <returns>The measurement, or null when it has not run here.</returns>
    public static Measured? For(IEnumerable<Measured> measured, string model, string? variant = null) =>
        measured.Where(m => m.Model == model && (variant is null || m.Variant == variant)).MaxBy(m => m.At);

    /// <summary>The line a card shows, e.g. "Measured here: 1.4 s per megapixel on DirectML, over 5 runs."</summary>
    /// <param name="measured">The measurement.</param>
    /// <returns>The line.</returns>
    public static string Line(Measured measured)
    {
        ArgumentNullException.ThrowIfNull(measured);
        string runs = measured.Runs == 1 ? "1 run" : $"{measured.Runs} runs";
        return string.Create(CultureInfo.CurrentCulture, $"Measured here: {measured.SecondsPerMegapixel:0.0#} s per megapixel on {measured.Provider}, over {runs}.");
    }
}
