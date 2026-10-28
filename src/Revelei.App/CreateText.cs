using System.Globalization;
using Revelei.Generation;

namespace Revelei.App;

/// <summary>
/// PB80: a generation's progress in the words the Create screen uses: the stage, the step of the
/// steps, the time taken and, once a step has finished, the time left at the pace since then (the
/// first step also moves the weights to the GPU, so it is not the pace).
/// </summary>
public static class CreateText
{
    /// <summary>The status line.</summary>
    /// <param name="progress">Where the generation is.</param>
    /// <param name="elapsed">Time since Generate.</param>
    /// <param name="sinceFirstStep">Time since the first sampling step finished, or null before it has.</param>
    /// <returns>e.g. "Step 3 of 8 · 12 s · about 10 s left".</returns>
    public static string Line(GenerationProgress progress, TimeSpan elapsed, TimeSpan? sinceFirstStep)
    {
        string taken = elapsed.TotalSeconds < 60
            ? string.Create(CultureInfo.CurrentCulture, $"{elapsed.TotalSeconds:0} s")
            : string.Create(CultureInfo.CurrentCulture, $"{(int)elapsed.TotalMinutes} min {elapsed.Seconds} s");
        return progress.Stage switch
        {
            GenerationStage.Loading => $"Loading the model · {taken}",
            GenerationStage.Decoding => $"Turning it into pixels · {taken}",
            _ when sinceFirstStep is { } since && progress.Step > 1 =>
                string.Create(CultureInfo.CurrentCulture, $"Step {progress.Step} of {progress.Steps} · {taken} · {DownloadText.Left(since / (progress.Step - 1) * (progress.Steps - progress.Step))}"),
            _ => string.Create(CultureInfo.CurrentCulture, $"Step {progress.Step} of {progress.Steps} · {taken}"),
        };
    }
}
