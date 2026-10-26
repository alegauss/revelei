using System.Runtime.InteropServices;

namespace Revelei.Imaging;

/// <summary>
/// The versions an image went through, with undo and redo (PB28). Every operation pushes a new
/// version; undo and redo move a pointer; pushing after an undo drops the versions ahead of it.
/// Versions beyond the memory budget, other than the current one, are spilled as raw samples to a
/// temporary folder and read back when the pointer reaches them, so a long chain of 4x upscales
/// does not hold every intermediate in RAM.
/// </summary>
public sealed class ImageHistory : IDisposable
{
    /// <summary>Bytes of samples kept in memory before older versions are spilled: 1 GiB.</summary>
    public const long DefaultBudget = 1L << 30;

    private readonly List<Version> versions = [];
    private readonly long budget;
    private readonly string folder;
    private int current = -1;
    private int spills;

    /// <summary>An empty history.</summary>
    /// <param name="budget">Bytes of samples to keep in memory.</param>
    /// <param name="folder">Where spilled versions go; a new folder under %TEMP% when null.</param>
    public ImageHistory(long budget = DefaultBudget, string? folder = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budget);
        this.budget = budget;
        this.folder = folder ?? Path.Combine(Path.GetTempPath(), "Revelei", "history-" + Guid.NewGuid().ToString("N"));
    }

    /// <summary>The labels, oldest first, e.g. "Opened 0014.jpg", "Upscale 4×".</summary>
    public IReadOnlyList<string> Labels => [.. versions.Select(v => v.Label)];

    /// <summary>Which version is shown, or -1 when empty.</summary>
    public int Position => current;

    /// <summary>Whether there is a version before the current one.</summary>
    public bool CanUndo => current > 0;

    /// <summary>Whether an undo can be redone.</summary>
    public bool CanRedo => current >= 0 && current < versions.Count - 1;

    /// <summary>How many versions sit on disk rather than in memory.</summary>
    public int Spilled => versions.Count(v => v.Image is null);

    /// <summary>The first version, the opened file: what a comparison shows as "before" (PB27).</summary>
    public RgbaImage? Original => versions.Count == 0 ? null : Load(versions[0]);

    /// <summary>The current version, read back from disk if it had been spilled.</summary>
    public RgbaImage? Current => current < 0 ? null : Load(versions[current]);

    /// <summary>Starts again from one image, as opening a new file does.</summary>
    /// <param name="image">The opened image.</param>
    /// <param name="label">Its history entry.</param>
    public void Reset(RgbaImage image, string label)
    {
        foreach (Version version in versions)
        {
            Forget(version);
        }

        versions.Clear();
        current = -1;
        Push(image, label);
    }

    /// <summary>Adds a version after the current one, dropping any that had been undone.</summary>
    /// <param name="image">The new version.</param>
    /// <param name="label">Its history entry.</param>
    public void Push(RgbaImage image, string label)
    {
        ArgumentNullException.ThrowIfNull(image);
        for (int i = versions.Count - 1; i > current; i--)
        {
            Forget(versions[i]);
            versions.RemoveAt(i);
        }

        versions.Add(new Version(label, image));
        current = versions.Count - 1;
        Trim();
    }

    /// <summary>Steps back one version.</summary>
    /// <returns>The version now current, or null when there was nothing to undo.</returns>
    public RgbaImage? Undo()
    {
        if (!CanUndo)
        {
            return null;
        }

        current--;
        RgbaImage image = Load(versions[current]);
        Trim();
        return image;
    }

    /// <summary>Steps forward again.</summary>
    /// <returns>The version now current, or null when there was nothing to redo.</returns>
    public RgbaImage? Redo()
    {
        if (!CanRedo)
        {
            return null;
        }

        current++;
        RgbaImage image = Load(versions[current]);
        Trim();
        return image;
    }

    /// <summary>Deletes every spilled file.</summary>
    public void Dispose()
    {
        foreach (Version version in versions)
        {
            Forget(version);
        }

        versions.Clear();
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Spills the versions farthest from the current one until memory fits the budget.</summary>
    private void Trim()
    {
        long held = versions.Where(v => v.Image is not null).Sum(v => v.Bytes);
        foreach (int i in Enumerable.Range(0, versions.Count).OrderByDescending(i => Math.Abs(i - current)))
        {
            if (held <= budget)
            {
                return;
            }

            Version version = versions[i];
            if (i == current || version.Image is null)
            {
                continue;
            }

            Directory.CreateDirectory(folder);
            version.Path ??= System.IO.Path.Combine(folder, $"{spills++}.raw");
            if (!File.Exists(version.Path))
            {
                File.WriteAllBytes(version.Path, MemoryMarshal.AsBytes(version.Image.Samples).ToArray());
            }

            held -= version.Bytes;
            version.Image = null;
        }
    }

    private static RgbaImage Load(Version version)
    {
        if (version.Image is { } image)
        {
            return image;
        }

        RgbaImage loaded = new(version.Width, version.Height, version.Depth);
        byte[] bytes = File.ReadAllBytes(version.Path!);
        MemoryMarshal.Cast<byte, ushort>(bytes).CopyTo(loaded.Samples);
        version.Image = loaded;
        return loaded;
    }

    private static void Forget(Version version)
    {
        if (version.Path is { } path && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed class Version(string label, RgbaImage image)
    {
        public string Label { get; } = label;

        public int Width { get; } = image.Width;

        public int Height { get; } = image.Height;

        public int Depth { get; } = image.SourceBitDepth;

        public long Bytes { get; } = (long)image.Width * image.Height * 8;

        public RgbaImage? Image { get; set; } = image;

        public string? Path { get; set; }
    }
}
