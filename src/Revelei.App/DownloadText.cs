using System.Globalization;
using Revelei.Inference;

namespace Revelei.App;

/// <summary>
/// PB58: a download's numbers in the words the cards, the consent panel and the status bar use,
/// so all three say the same thing: megabytes done of the total, the speed, and the time left.
/// </summary>
public static class DownloadText
{
    /// <summary>A size, in MB below a gigabyte and in GB from there.</summary>
    /// <param name="bytes">The size.</param>
    /// <returns>e.g. 325 MB or 1.2 GB.</returns>
    public static string Size(long bytes) => bytes >= 1L << 30
        ? string.Create(CultureInfo.CurrentCulture, $"{bytes / (double)(1L << 30):0.0} GB")
        : string.Create(CultureInfo.CurrentCulture, $"{Math.Max(1, (bytes + (1 << 19)) >> 20)} MB");

    /// <summary>The time left, rounded the way a person reads it.</summary>
    /// <param name="left">The estimate.</param>
    /// <returns>e.g. about 40 s left, about 3 min left.</returns>
    public static string Left(TimeSpan left) => left.TotalSeconds < 60
        ? string.Create(CultureInfo.CurrentCulture, $"about {Math.Max(5, Math.Ceiling(left.TotalSeconds / 5) * 5):0} s left")
        : string.Create(CultureInfo.CurrentCulture, $"about {Math.Ceiling(left.TotalMinutes):0} min left");

    /// <summary>One line for a download in progress.</summary>
    /// <param name="progress">Where it is.</param>
    /// <returns>e.g. 120 of 325 MB · 8.4 MB/s · about 25 s left, or Verifying… once the bytes are in.</returns>
    public static string Line(DownloadProgress progress) => progress.Stage switch
    {
        DownloadStage.Verifying => "Verifying…",
        DownloadStage.Retrying => $"{Done(progress)} · connection lost, trying again…",
        _ when progress.BytesPerSecond <= 0 => $"{Done(progress)} · starting…",
        _ => string.Create(CultureInfo.CurrentCulture, $"{Done(progress)} · {progress.BytesPerSecond / (1 << 20):0.0} MB/s · {Left(progress.Remaining!.Value)}"),
    };

    /// <summary>How much of the download is done, as a percentage.</summary>
    /// <param name="progress">Where it is.</param>
    /// <returns>0 to 100.</returns>
    public static int Percent(DownloadProgress progress) =>
        progress.Total <= 0 ? 0 : (int)Math.Clamp(100 * progress.Done / progress.Total, 0, 100);

    private static string Done(DownloadProgress progress) =>
        string.Create(CultureInfo.CurrentCulture, $"{progress.Done >> 20} of {Size(progress.Total)}");
}
