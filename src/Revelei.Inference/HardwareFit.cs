namespace Revelei.Inference;

/// <summary>What this PC has to run a model on (PB72).</summary>
/// <param name="VideoBytes">The best GPU's dedicated video memory, or 0 with no usable GPU.</param>
/// <param name="SystemBytes">Physical RAM.</param>
/// <param name="FreeDiskBytes">Free space on the drive models download to.</param>
public sealed record MachineProfile(long VideoBytes, long SystemBytes, long FreeDiskBytes)
{
    /// <summary>Measures this PC: the GPU DirectML would pick, the RAM, and the models drive.</summary>
    /// <param name="modelsFolder">The folder downloads go to, which need not exist yet.</param>
    /// <returns>The profile.</returns>
    public static MachineProfile Measure(string modelsFolder)
    {
        long video = GpuAdapters.Best(GpuAdapters.Enumerate())?.DedicatedVideoMemory ?? 0;
        long ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        long disk = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(modelsFolder))!).AvailableFreeSpace;
        return new MachineProfile(video, ram, disk);
    }
}

/// <summary>How a model would run here, best first.</summary>
public enum Fit
{
    /// <summary>Fits in video memory with a margin.</summary>
    RunsWell,

    /// <summary>Fits only by offloading part of it to RAM.</summary>
    RunsSlower,

    /// <summary>No usable GPU, but enough RAM to run on the CPU.</summary>
    CpuOnly,

    /// <summary>Short of RAM or disk.</summary>
    DoesNotFit,
}

/// <summary>A verdict and the reason, in words a person reads before downloading.</summary>
/// <param name="Fit">The verdict.</param>
/// <param name="Reason">Why, with the numbers.</param>
/// <param name="Slowdown">For <see cref="Fit.RunsSlower"/>, roughly how many times slower than a full-GPU run; otherwise 1.</param>
public sealed record FitVerdict(Fit Fit, string Reason, double Slowdown = 1);

/// <summary>
/// PB72: whether a model will run well, slowly or not at all on this PC, before its gigabytes
/// are downloaded. The catalog (PB70) gives each quantisation the video memory a full-GPU run
/// needs and the RAM it needs beside; where it says nothing, as for today's small ONNX models,
/// the files' size stands for both. These are estimates until the model has run here (PB76).
/// </summary>
public static class HardwareFit
{
    /// <summary>The share of video memory a model may take and still run well: the rest is the margin.</summary>
    public const double Margin = 0.9;

    /// <summary>How much slower a run gets per share of the model offloaded to RAM, on top of the full-GPU run.</summary>
    public const double OffloadPenalty = 3;

    /// <summary>The verdict for one quantisation of a model.</summary>
    /// <param name="quantisation">The quantisation, as the catalog lists it.</param>
    /// <param name="machine">This PC.</param>
    /// <param name="installed">True when its files are already on disk, so disk is not asked of it again.</param>
    /// <returns>The verdict.</returns>
    public static FitVerdict Judge(ManifestQuantisation quantisation, MachineProfile machine, bool installed = false)
    {
        ArgumentNullException.ThrowIfNull(quantisation);
        ArgumentNullException.ThrowIfNull(machine);
        long disk = quantisation.Files.Sum(f => f.File.Bytes);
        long video = quantisation.Memory?.VideoBytes ?? disk;
        long system = quantisation.Memory?.SystemBytes ?? disk;

        if (!installed && machine.FreeDiskBytes < disk)
        {
            return new(Fit.DoesNotFit, $"Needs {Size(disk)} of disk and the models drive has {Size(machine.FreeDiskBytes)} free.");
        }

        long usable = (long)(machine.VideoBytes * Margin);
        if (machine.VideoBytes > 0 && video <= usable && system <= machine.SystemBytes)
        {
            return new(Fit.RunsWell, $"Fits in the GPU's {Size(machine.VideoBytes)} of video memory.");
        }

        if (machine.VideoBytes > 0 && system + (video - usable) <= machine.SystemBytes)
        {
            double offloaded = (double)(video - usable) / video;
            double slowdown = Math.Round((1 + (OffloadPenalty * offloaded)) * 2, MidpointRounding.AwayFromZero) / 2;
            return new(
                Fit.RunsSlower,
                $"Needs {Size(video)} of video memory and the GPU has {Size(machine.VideoBytes)}, so {Size(video - usable)} is streamed from RAM: roughly {slowdown:0.#} times slower.",
                slowdown);
        }

        if (machine.VideoBytes == 0 && video + system <= machine.SystemBytes)
        {
            return new(Fit.CpuOnly, $"No GPU it can use, so it runs on the CPU from {Size(machine.SystemBytes)} of RAM: very slow.");
        }

        long missing = (machine.VideoBytes > 0 ? system + (video - usable) : video + system) - machine.SystemBytes;
        return new(Fit.DoesNotFit, $"Needs {Size(missing)} more RAM than this PC's {Size(machine.SystemBytes)}.");
    }

    /// <summary>
    /// The quantisation to offer first: the largest that runs well, or failing that the one that
    /// runs with the least offloading, or failing that the smallest that runs at all; null when
    /// none fits.
    /// </summary>
    /// <param name="model">The catalog's model.</param>
    /// <param name="machine">This PC.</param>
    /// <returns>The quantisation's name and verdict, or null.</returns>
    public static (string Quantisation, FitVerdict Verdict)? Recommend(ManifestModel model, MachineProfile machine)
    {
        ArgumentNullException.ThrowIfNull(model);
        (string Name, FitVerdict Verdict, long Bytes)[] judged = [.. (model.Quantisations ?? new Dictionary<string, ManifestQuantisation>())
            .Select(q => (q.Key, Judge(q.Value, machine), q.Value.Files.Sum(f => f.File.Bytes)))];
        (string Name, FitVerdict Verdict, long Bytes)? best =
            judged.Where(j => j.Verdict.Fit == Fit.RunsWell).OrderByDescending(j => j.Bytes).Cast<(string, FitVerdict, long)?>().FirstOrDefault()
            ?? judged.Where(j => j.Verdict.Fit is Fit.RunsSlower or Fit.CpuOnly).OrderBy(j => j.Verdict.Fit).ThenBy(j => j.Verdict.Slowdown).ThenBy(j => j.Bytes).Cast<(string, FitVerdict, long)?>().FirstOrDefault();
        return best is { } chosen ? (chosen.Name, chosen.Verdict) : null;
    }

    private static string Size(long bytes) => bytes < 1L << 30
        ? $"{Math.Max(1, bytes >> 20)} MB"
        : $"{bytes / (double)(1L << 30):0.#} GB";
}
