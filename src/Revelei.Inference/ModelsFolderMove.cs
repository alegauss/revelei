namespace Revelei.Inference;

/// <summary>
/// PB74: moves the downloaded models to a folder the person chose, so a 15 GB model need not
/// fill the system drive. Every file is copied, each copy is checked by size, and only then is
/// the old copy deleted, so a cancel, a full drive or a failure midway leaves the models where
/// they were and removes the half-made copy. Partial downloads (.part) and the queue move with
/// them, so an interrupted download resumes in the new place.
/// </summary>
public static class ModelsFolderMove
{
    /// <summary>Moves everything in <paramref name="from"/> into <paramref name="to"/>.</summary>
    /// <param name="from">The current models folder; nothing happens when it does not exist.</param>
    /// <param name="to">The chosen folder, created when missing; it must not be inside <paramref name="from"/> or hold files of the same name.</param>
    /// <param name="progress">Told the bytes copied so far and the total, after each file.</param>
    /// <param name="cancellation">Stops between files; the copy made so far is removed.</param>
    /// <returns>The bytes moved.</returns>
    /// <exception cref="ArgumentException">The two are the same folder, or the new one is inside the old.</exception>
    /// <exception cref="NotEnoughDiskException">The chosen drive cannot hold the models.</exception>
    /// <exception cref="IOException">The chosen folder already holds a file of the same name.</exception>
    public static async Task<long> MoveAsync(string from, string to, IProgress<(long Done, long Total)>? progress, CancellationToken cancellation)
    {
        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(from));
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(to));
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The models are already in that folder.", nameof(to));
        }

        if (target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The new folder cannot be inside the current one.", nameof(to));
        }

        if (!Directory.Exists(source))
        {
            return 0;
        }

        FileInfo[] files = new DirectoryInfo(source).GetFiles("*", SearchOption.AllDirectories);
        string[] targets = [.. files.Select(f => Path.Combine(target, Path.GetRelativePath(source, f.FullName)))];
        if (targets.FirstOrDefault(File.Exists) is { } taken)
        {
            throw new IOException($"{taken} already exists; choose an empty folder.");
        }

        long total = files.Sum(f => f.Length);
        string drive = Path.GetPathRoot(target)!;
        long free = ModelDownloads.FreeSpace.Value?.Invoke(drive) ?? new DriveInfo(drive).AvailableFreeSpace;
        bool sameDrive = string.Equals(Path.GetPathRoot(source), drive, StringComparison.OrdinalIgnoreCase);
        if (!sameDrive && free < total + ModelDownloads.DiskMargin)
        {
            throw new NotEnoughDiskException(total + ModelDownloads.DiskMargin, free);
        }

        List<string> copied = [];
        long done = 0;
        try
        {
            for (int i = 0; i < files.Length; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(targets[i])!);
                copied.Add(targets[i]);
                await using (FileStream input = new(files[i].FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
                await using (FileStream output = new(targets[i], FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                {
                    await input.CopyToAsync(output, cancellation).ConfigureAwait(false);
                }

                if (new FileInfo(targets[i]).Length != files[i].Length)
                {
                    throw new IOException($"{targets[i]} came out a different size from {files[i].FullName}.");
                }

                done += files[i].Length;
                progress?.Report((done, total));
            }
        }
        catch
        {
            foreach (string made in copied.Where(File.Exists))
            {
                File.Delete(made);
            }

            throw;
        }

        Directory.Delete(source, recursive: true);
        return total;
    }
}
