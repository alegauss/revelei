using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Revelei.Inference;

/// <summary>One file the manifest lists: where it is, and what it must be.</summary>
/// <param name="Url">A resolve/&lt;commit&gt;/ URL, so its bytes cannot change under the app.</param>
/// <param name="Bytes">Its size.</param>
/// <param name="Sha256">Its SHA-256, lowercase hex.</param>
public sealed record ManifestFile(string Url, long Bytes, string Sha256);

/// <summary>One file of a schema 2 quantisation, and what it is for (PB70).</summary>
/// <param name="Role">weights for an ONNX model; transformer, text-encoder or vae for a diffusion one.</param>
/// <param name="File">Where it is and what it must be.</param>
public sealed record ManifestPart(string Role, ManifestFile File);

/// <summary>What a quantisation needs to run, for the hardware-fit verdict (PB72).</summary>
/// <param name="VideoBytes">Video memory it needs to run without spilling into RAM.</param>
/// <param name="SystemBytes">System memory it needs.</param>
public sealed record ManifestMemory(long VideoBytes, long SystemBytes);

/// <summary>One way to download a model (fp32, fp16, q4...): its files and what it needs.</summary>
/// <param name="Files">Its files, by role.</param>
/// <param name="Memory">What it needs to run, or null where the catalog does not say.</param>
public sealed record ManifestQuantisation(IReadOnlyList<ManifestPart> Files, ManifestMemory? Memory = null);

/// <summary>What runs a model.</summary>
public enum ModelKind
{
    /// <summary>One ONNX file through Windows ML: today's photo operations.</summary>
    Onnx,

    /// <summary>A text-to-image model of several files (block J).</summary>
    Diffusion,
}

/// <summary>One model the manifest lists.</summary>
/// <param name="Name">Its folder name.</param>
/// <param name="Meta">Its meta.json.</param>
/// <param name="Variants">Its ONNX files, by variant.</param>
/// <param name="License">Its license, as the About panel shows it.</param>
/// <param name="ModelVersion">PB70: the model's own version, which an update compares; null in schema 1.</param>
/// <param name="Kind">PB70: what runs it.</param>
/// <param name="NonCommercial">PB70: whether its license forbids commercial use.</param>
/// <param name="Quantisations">PB70: every way to download it; schema 1's variants read as weights alone.</param>
/// <param name="Note">PB71: one line on what this version changed, which an update shows.</param>
public sealed record ManifestModel(
    string Name,
    ManifestFile Meta,
    IReadOnlyDictionary<string, ManifestFile> Variants,
    string License,
    string? ModelVersion = null,
    ModelKind Kind = ModelKind.Onnx,
    bool NonCommercial = false,
    IReadOnlyDictionary<string, ManifestQuantisation>? Quantisations = null,
    string? Note = null);

/// <summary>
/// scripts/build-manifest.py's manifest.json (PB12), schema 1, or the catalog PB70 publishes at a
/// stable path, schema 2, which adds versions, kinds, files by role and memory per quantisation.
/// </summary>
/// <param name="Schema">The format version.</param>
/// <param name="Version">The manifest's own version.</param>
/// <param name="Models">The models it lists.</param>
/// <param name="Sequence">
/// PB70: only ever grows, so a fetched catalog lower than the embedded one is a replay and is
/// refused (<see cref="ModelCatalog"/>). Schema 1 has none and reads as 0.
/// </param>
public sealed record ModelManifest(int Schema, string Version, IReadOnlyList<ManifestModel> Models, long Sequence = 0)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        RespectRequiredConstructorParameters = true,
    };

    /// <summary>Reads a manifest and refuses one this build cannot trust.</summary>
    /// <param name="json">manifest.json's text.</param>
    /// <param name="loopback">
    /// PB59: also accept plain http on this machine's own loopback, for a manifest a person passed
    /// with --manifest, such as the UI cases' fixture server's. The pinned manifest never does.
    /// </param>
    /// <returns>The manifest.</returns>
    /// <exception cref="InvalidDataException">It is not schema 1 or 2, or a URL is not https.</exception>
    public static ModelManifest Parse(string json, bool loopback = false)
    {
        ModelManifest manifest;
        try
        {
            int schema = JsonSerializer.Deserialize<SchemaOnly>(json, Options)?.Schema ?? throw new InvalidDataException("The manifest is empty.");
            manifest = schema switch
            {
                1 => Schema1(JsonSerializer.Deserialize<ModelManifest>(json, Options)!),
                2 => JsonSerializer.Deserialize<Catalog2>(json, Options)!.Read(),
                _ => throw new InvalidDataException($"The manifest is schema {schema}; this build reads 1 and 2."),
            };
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"The manifest is not valid: {error.Message}", error);
        }

        if (manifest.Models.SelectMany(Listed).FirstOrDefault(f => !Trusted(f.Url, loopback)) is { } plain)
        {
            throw new InvalidDataException($"The manifest lists {plain.Url}, which is not https.");
        }

        return manifest;
    }

    private static IEnumerable<ManifestFile> Listed(ManifestModel model) =>
        model.Variants.Values
            .Concat((model.Quantisations ?? new Dictionary<string, ManifestQuantisation>()).Values.SelectMany(q => q.Files.Select(p => p.File)))
            .Append(model.Meta);

    // Schema 1 lists one ONNX file per variant; read as a quantisation of weights alone, so
    // whatever reads quantisations reads both schemas alike.
    private static ModelManifest Schema1(ModelManifest manifest) => manifest with
    {
        Models = [.. manifest.Models.Select(m => m with
        {
            Quantisations = m.Variants.ToDictionary(v => v.Key, v => new ManifestQuantisation([new ManifestPart(Weights, v.Value)])),
        })],
    };

    private const string Weights = "weights";

    private sealed record SchemaOnly(int Schema);

    private sealed record Catalog2(int Schema, string Version, long Sequence, IReadOnlyList<Model2> Models)
    {
        public ModelManifest Read() => new(Schema, Version, [.. Models.Select(m => m.Read())], Sequence);
    }

    private sealed record Model2(
        string Name,
        string Version,
        string Kind,
        ManifestFile Meta,
        IReadOnlyDictionary<string, ManifestQuantisation> Quantisations,
        string License,
        bool NonCommercial = false,
        string? Note = null)
    {
        public ManifestModel Read()
        {
            ModelKind kind = Kind switch
            {
                "onnx" => ModelKind.Onnx,
                "diffusion" => ModelKind.Diffusion,
                _ => throw new InvalidDataException($"{Name} is of kind {Kind}, which this build cannot run."),
            };

            // An ONNX model is still one file per quantisation, which is what downloads fetch today.
            Dictionary<string, ManifestFile> variants = [];
            if (kind == ModelKind.Onnx)
            {
                foreach ((string quantisation, ManifestQuantisation listed) in Quantisations)
                {
                    variants[quantisation] = listed.Files.SingleOrDefault(p => p.Role == Weights)?.File
                        ?? throw new InvalidDataException($"{Name} {quantisation} must list exactly one weights file.");
                }
            }

            return new ManifestModel(Name, Meta, variants, License, Version, kind, NonCommercial, Quantisations, Note);
        }
    }

    private static bool Trusted(string url, bool loopback) =>
        url.StartsWith("https://", StringComparison.Ordinal)
        || (loopback && Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) && parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback);

    /// <summary>A model by name, or null.</summary>
    /// <param name="name">Its folder name.</param>
    /// <returns>The model.</returns>
    public ManifestModel? Model(string name) => Models.FirstOrDefault(m => m.Name == name);
}

/// <summary>PB73: the drive cannot hold a download, said before its first byte.</summary>
/// <param name="needed">Bytes the download needs, the margin included.</param>
/// <param name="free">Bytes free on the drive.</param>
public sealed class NotEnoughDiskException(long needed, long free)
    : IOException($"The download needs {needed} bytes and the drive has {free} free.")
{
    /// <summary>Bytes the download needs, the margin included.</summary>
    public long Needed { get; } = needed;

    /// <summary>Bytes free on the drive.</summary>
    public long Free { get; } = free;
}

/// <summary>What a download is doing.</summary>
public enum DownloadStage
{
    /// <summary>Fetching bytes.</summary>
    Downloading,

    /// <summary>Checking a fetched file's SHA-256.</summary>
    Verifying,

    /// <summary>Waiting to try again after a network failure.</summary>
    Retrying,
}

/// <summary>How far a download is, for the progress bar and its words.</summary>
/// <param name="File">The file being fetched, e.g. gfpgan-v1.4/fp32.onnx.</param>
/// <param name="Done">Bytes on disk across the whole download, resumed bytes included.</param>
/// <param name="Total">Bytes the whole download needs.</param>
/// <param name="Stage">Fetching, verifying or waiting to retry.</param>
/// <param name="BytesPerSecond">The recent speed, or 0 before there is one.</param>
public readonly record struct DownloadProgress(string File, long Done, long Total, DownloadStage Stage = DownloadStage.Downloading, double BytesPerSecond = 0)
{
    /// <summary>The time left at the recent speed, or null before there is a speed.</summary>
    public TimeSpan? Remaining => BytesPerSecond > 0 ? TimeSpan.FromSeconds((Total - Done) / BytesPerSecond) : null;
}

/// <summary>What a download did.</summary>
/// <param name="Fetched">Files fetched and verified.</param>
/// <param name="Present">Files already on disk with the right bytes.</param>
public sealed record DownloadResult(int Fetched, int Present);

/// <summary>
/// Models fetched from the manifest PB12 published (PB13, PB58): the optional FP32 set, and the
/// models face restore and background removal fetch on first use. Each file is streamed to a
/// .part file, checked against the manifest's size and SHA-256, and only then renamed into place,
/// so ONNX Runtime never loads a file that arrived corrupt; one that fails is deleted and named.
/// A file already present with the right bytes, here or in the installer's folder, is not fetched.
/// A cancelled or interrupted file keeps its .part and resumes by HTTP Range on the next try, and
/// a network failure is retried with a growing pause before it is reported.
/// </summary>
public static class ModelDownloads
{
    /// <summary>
    /// The manifest this build pins, scripts/published-models.json's manifest_url: a
    /// resolve/&lt;commit&gt;/ URL, so the files it lists cannot change under a released app.
    /// </summary>
    public const string ManifestUrl = "https://huggingface.co/alegauss/photo-boost-models/resolve/f8a6fa2a88ae1a3a605d749d891a786e05bec839/manifest.json";

    /// <summary>The variant every manifest file is.</summary>
    public const string Variant = "fp32";

    /// <summary>How many times a file is tried after a network failure before it is reported.</summary>
    public const int Attempts = 4;

    /// <summary>How long the connection may stay silent before the attempt is given up and retried.</summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    private static readonly Lazy<ModelManifest> EmbeddedManifest = new(() =>
    {
        using Stream stream = typeof(ModelDownloads).Assembly.GetManifestResourceStream("Revelei.Inference.models-manifest.json")
            ?? throw new InvalidDataException("This build carries no models manifest.");
        using StreamReader reader = new(stream);
        return ModelManifest.Parse(reader.ReadToEnd());
    });

    /// <summary>
    /// PB58: the pinned manifest as this build carries it, the very file published at
    /// <see cref="ManifestUrl"/>, so every size and hash is known without the network.
    /// </summary>
    public static ModelManifest Embedded => EmbeddedManifest.Value;

    /// <summary>Bytes the variant needs across the manifest, or across the named models.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <param name="models">Only these models, or null for every one.</param>
    /// <returns>The total, meta.json files included.</returns>
    public static long TotalBytes(ModelManifest manifest, IReadOnlyCollection<string>? models = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return Selected(manifest, models).Sum(m => m.Meta.Bytes + (m.Variants.TryGetValue(Variant, out ManifestFile? f) ? f.Bytes : 0));
    }

    /// <summary>Whether every file the variant needs is on disk with the right bytes.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <param name="root">The folder downloads go to.</param>
    /// <param name="elsewhere">Other models folders whose identical file counts, such as the installer's.</param>
    /// <param name="models">Only these models, or null for every one.</param>
    /// <returns>True when nothing is left to fetch.</returns>
    public static bool Complete(ModelManifest manifest, string root, IReadOnlyList<string>? elsewhere = null, IReadOnlyCollection<string>? models = null) =>
        Files(manifest, root, models).All(file => Present(file.Path, file.Label, file.Expected, elsewhere));

    /// <summary>Bytes still to fetch: what <see cref="DownloadAsync"/> would download now.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <param name="root">The folder downloads go to.</param>
    /// <param name="elsewhere">Other models folders whose identical file counts.</param>
    /// <param name="models">Only these models, or null for every one.</param>
    /// <returns>The bytes, a partial file's resumed bytes not counted again.</returns>
    public static long MissingBytes(ModelManifest manifest, string root, IReadOnlyList<string>? elsewhere = null, IReadOnlyCollection<string>? models = null) =>
        Files(manifest, root, models)
            .Where(file => !Present(file.Path, file.Label, file.Expected, elsewhere))
            .Sum(file => file.Expected.Bytes - Math.Min(PartialBytes(file.Path), file.Expected.Bytes));

    /// <summary>
    /// <see cref="MissingBytes"/> by size alone, for what a card or the About panel shows: cheap
    /// enough for every refresh, where hashing 325 MB is not. The download itself still hashes.
    /// </summary>
    /// <param name="manifest">The manifest.</param>
    /// <param name="root">The folder downloads go to.</param>
    /// <param name="elsewhere">Other models folders whose file of the same size counts.</param>
    /// <param name="models">Only these models, or null for every one.</param>
    /// <returns>The bytes still to fetch, as far as sizes tell.</returns>
    public static long PendingBytes(ModelManifest manifest, string root, IReadOnlyList<string>? elsewhere = null, IReadOnlyCollection<string>? models = null) =>
        Files(manifest, root, models)
            .Where(file => !Sized(file.Path, file.Expected) && !(elsewhere ?? []).Any(other => Sized(Path.Combine(other, file.Label), file.Expected)))
            .Sum(file => file.Expected.Bytes - Math.Min(PartialBytes(file.Path), file.Expected.Bytes));

    /// <summary>Fetches every file the variant needs that is not on disk with the right bytes.</summary>
    /// <param name="http">The client.</param>
    /// <param name="manifest">The manifest.</param>
    /// <param name="root">The folder downloads go to, e.g. %LOCALAPPDATA%\Revelei\models.</param>
    /// <param name="progress">Told after each chunk, and when a file is verified or retried.</param>
    /// <param name="cancellation">Cancels between chunks; the .part file stays, so a later download resumes it.</param>
    /// <param name="elsewhere">
    /// Other models folders: a file already there with the manifest's bytes is not fetched, so
    /// GFPGAN's FP32, which the installer carried from PB54 to PB58, is not downloaded a second time.
    /// </param>
    /// <param name="models">Only these models, or null for every one.</param>
    /// <param name="retryDelay">The first pause before a retry, doubled each time; one second unless set.</param>
    /// <returns>How many files were fetched and how many were already there.</returns>
    /// <exception cref="InvalidDataException">A file arrived with another size or SHA-256; it was deleted.</exception>
    /// <exception cref="HttpRequestException">The server could not be reached after every attempt.</exception>
    public static async Task<DownloadResult> DownloadAsync(
        HttpClient http,
        ModelManifest manifest,
        string root,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellation,
        IReadOnlyList<string>? elsewhere = null,
        IReadOnlyCollection<string>? models = null,
        TimeSpan? retryDelay = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(manifest);
        EnsureDisk(manifest, root, elsewhere, models);
        long total = TotalBytes(manifest, models);
        long done = 0;
        int fetched = 0, present = 0;
        HashSet<string> atRoot = new(StringComparer.OrdinalIgnoreCase), fetchedNow = new(StringComparer.OrdinalIgnoreCase);
        Speed speed = new();
        foreach ((string path, string label, ManifestFile expected) in Files(manifest, root, models))
        {
            bool here = Valid(path, expected);
            if (here)
            {
                atRoot.Add(path);
            }

            if (here || (elsewhere ?? []).Any(other => Valid(Path.Combine(other, label), expected)))
            {
                present++;
                done += expected.Bytes;
                progress?.Report(new DownloadProgress(label, done, total));
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string part = path + ".part";
            long before = done;
            TimeSpan pause = retryDelay ?? TimeSpan.FromSeconds(1);
            string? digest;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    digest = await FetchAsync(http, expected, part, label, before, total, speed, progress, cancellation).ConfigureAwait(false);
                    break;
                }
                catch (Exception failure) when (Transient(failure) && !cancellation.IsCancellationRequested && attempt < Attempts)
                {
                    progress?.Report(new DownloadProgress(label, before + PartialBytes(path), total, DownloadStage.Retrying));
                    await Task.Delay(pause, cancellation).ConfigureAwait(false);
                    pause *= 2;
                }
            }

            progress?.Report(new DownloadProgress(label, before + expected.Bytes, total, DownloadStage.Verifying, speed.BytesPerSecond));
            bool valid = digest is null
                ? Valid(part, expected)
                : new FileInfo(part).Length == expected.Bytes && digest == expected.Sha256;
            if (!valid)
            {
                File.Delete(part);
                throw new InvalidDataException($"{label} arrived different from the manifest (size or SHA-256), so it was deleted and nothing was loaded.");
            }

            File.Move(part, path, overwrite: true);
            atRoot.Add(path);
            fetchedNow.Add(path);
            fetched++;
            done = before + expected.Bytes;
        }

        Record(manifest, root, models, atRoot, fetchedNow);
        return new DownloadResult(fetched, present);
    }

    // One attempt at one file: resumes from the .part when the server honours a range, and
    // starts over when it does not or when the .part is already as long as the file should be.
    // PB73: returns the SHA-256 hashed while streaming, the resumed bytes read once to seed it, so
    // a 12 GB file is not read a second time to verify it; null when nothing was fetched.
    private static async Task<string?> FetchAsync(HttpClient http, ManifestFile expected, string part, string label, long before, long total, Speed speed, IProgress<DownloadProgress>? progress, CancellationToken cancellation)
    {
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have >= expected.Bytes)
        {
            if (have == expected.Bytes)
            {
                return null; // Fetched last time and never verified; the caller verifies it now.
            }

            File.Delete(part);
            have = 0;
        }

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(expected.Url));
        if (have > 0)
        {
            request.Headers.Range = new RangeHeaderValue(have, null);
        }

        // The client has no timeout, since a 325 MB file may take many minutes; what fails is a
        // connection that goes silent, so each wait gets its own limit and a stall is retried.
        using CancellationTokenSource stall = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        stall.CancelAfter(StallTimeout);
        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            bool resumed = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (!resumed)
            {
                have = 0;
            }

            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            if (resumed)
            {
                await using FileStream seed = new(part, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
                byte[] chunk = new byte[1 << 20];
                int got;
                while ((got = await seed.ReadAsync(chunk, cancellation).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(chunk, 0, got);
                }
            }

            await using Stream source = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            await using FileStream target = new(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            byte[] buffer = new byte[1 << 20];
            int read;
            while ((read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
            {
                stall.CancelAfter(StallTimeout);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                have += read;
                speed.Add(read);
                progress?.Report(new DownloadProgress(label, before + have, total, DownloadStage.Downloading, speed.BytesPerSecond));
            }

            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        catch (OperationCanceledException silent) when (!cancellation.IsCancellationRequested)
        {
            throw new IOException($"The connection went silent for {StallTimeout.TotalSeconds:0} seconds.", silent);
        }
    }

    /// <summary>Room kept free beyond the files themselves, so a download never fills the drive to the last byte.</summary>
    public const long DiskMargin = 256L << 20;

    // Tests set this to stand for a drive's free space; it flows only through their own awaits.
    internal static readonly AsyncLocal<Func<string, long>?> FreeSpace = new();

    // PB73: refuses before the first byte when the drive cannot hold what is left plus a margin,
    // where a 12 GB download would otherwise fail with a full disk most of the way through.
    private static void EnsureDisk(ModelManifest manifest, string root, IReadOnlyList<string>? elsewhere, IReadOnlyCollection<string>? models)
    {
        long needed = PendingBytes(manifest, root, elsewhere, models);
        if (needed == 0)
        {
            return;
        }

        string drive = Path.GetPathRoot(Path.GetFullPath(root))!;
        long free = FreeSpace.Value?.Invoke(drive) ?? new DriveInfo(drive).AvailableFreeSpace;
        if (free < needed + DiskMargin)
        {
            throw new NotEnoughDiskException(needed + DiskMargin, free);
        }
    }

    // PB71: installed.json for each model whose files are all in the download folder, written
    // when one of them was fetched now or the folder has no record yet, so an update can be told.
    private static void Record(ModelManifest manifest, string root, IReadOnlyCollection<string>? models, HashSet<string> atRoot, HashSet<string> fetchedNow)
    {
        foreach (ManifestModel model in Selected(manifest, models))
        {
            string[] paths = [.. Files(manifest, root, [model.Name]).Select(f => f.Path)];
            string folder = Path.Combine(root, model.Name);
            if (!model.Variants.TryGetValue(Variant, out ManifestFile? weights)
                || !paths.All(atRoot.Contains)
                || (!paths.Any(fetchedNow.Contains) && InstalledRecord.Read(folder) is not null))
            {
                continue;
            }

            Dictionary<string, string> files = new(StringComparer.Ordinal) { [Variant + ".onnx"] = weights.Sha256, ["meta.json"] = model.Meta.Sha256 };
            new InstalledRecord(ModelUpdates.VersionOf(model), Variant, DateTimeOffset.Now, files).Write(folder);
        }
    }

    // A dropped or silent connection and a server error can pass; a 4xx is the file not being
    // there, which no retry changes (PB59), so it is reported at once.
    private static bool Transient(Exception failure) => failure switch
    {
        HttpRequestException { StatusCode: { } status } => (int)status >= 500,
        HttpRequestException or IOException => true,
        _ => false,
    };

    private static IEnumerable<ManifestModel> Selected(ModelManifest manifest, IReadOnlyCollection<string>? models) =>
        models is null ? manifest.Models : manifest.Models.Where(m => models.Contains(m.Name));

    private static IEnumerable<(string Path, string Label, ManifestFile Expected)> Files(ModelManifest manifest, string root, IReadOnlyCollection<string>? models)
    {
        foreach (ManifestModel model in Selected(manifest, models))
        {
            if (model.Variants.TryGetValue(Variant, out ManifestFile? onnx))
            {
                yield return (Path.Combine(root, model.Name, Variant + ".onnx"), $"{model.Name}/{Variant}.onnx", onnx);
            }

            // After the ONNX file, so a model folder never holds a meta.json without its variant.
            yield return (Path.Combine(root, model.Name, "meta.json"), $"{model.Name}/meta.json", model.Meta);
        }
    }

    private static long PartialBytes(string path)
    {
        FileInfo part = new(path + ".part");
        return part.Exists ? part.Length : 0;
    }

    private static bool Present(string path, string label, ManifestFile expected, IReadOnlyList<string>? elsewhere) =>
        Valid(path, expected) || (elsewhere ?? []).Any(other => Valid(Path.Combine(other, label), expected));

    private static bool Sized(string path, ManifestFile expected)
    {
        FileInfo file = new(path);
        return file.Exists && file.Length == expected.Bytes;
    }

    private static bool Valid(string path, ManifestFile expected)
    {
        FileInfo file = new(path);
        if (!file.Exists || file.Length != expected.Bytes)
        {
            return false;
        }

        using FileStream stream = file.OpenRead();
        return Convert.ToHexStringLower(SHA256.HashData(stream)) == expected.Sha256;
    }

    /// <summary>The recent speed: bytes over the last few seconds, so a stall shows and a burst does not mislead.</summary>
    private sealed class Speed
    {
        private static readonly TimeSpan Window = TimeSpan.FromSeconds(4);
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Queue<(TimeSpan At, long Bytes)> samples = new();
        private long inWindow;

        public double BytesPerSecond { get; private set; }

        public void Add(long bytes)
        {
            TimeSpan now = clock.Elapsed;
            samples.Enqueue((now, bytes));
            inWindow += bytes;
            while (samples.Count > 1 && now - samples.Peek().At > Window)
            {
                inWindow -= samples.Dequeue().Bytes;
            }

            double seconds = (now - samples.Peek().At).TotalSeconds;
            BytesPerSecond = seconds > 0.5 ? inWindow / seconds : 0;
        }
    }
}
