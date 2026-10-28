using Revelei.Inference.Pipelines;

namespace Revelei.Inference;

/// <summary>An operation whose models the installer leaves out and its first use downloads (PB58).</summary>
/// <param name="Title">The card's title, e.g. Restore faces.</param>
/// <param name="Verb">What the button does after "Download and", e.g. restore faces.</param>
/// <param name="Primary">The model it cannot run without.</param>
/// <param name="Companions">Models it runs better with, fetched in the same download when missing.</param>
public sealed record FirstUseOperation(string Title, string Verb, string Primary, IReadOnlyList<string> Companions);

/// <summary>
/// PB58: what face restore and background removal fetch the first time they are used, from the
/// manifest the app embeds, so the card can say what, how large and from where before anything
/// is downloaded.
/// </summary>
public static class FirstUse
{
    /// <summary>GFPGAN, 325 MB; the detector YuNet stays in the installer.</summary>
    public static readonly FirstUseOperation Faces = new("Restore faces", "restore faces", FaceRestore.Model, []);

    /// <summary>RMBG-1.4 from BRIA, and ViTMatte for Refine edges.</summary>
    public static readonly FirstUseOperation Background = new("Remove background", "remove background", RemoveBackground.Model, [Matting.Model]);

    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        [FaceRestore.Model] = "GFPGAN v1.4, the face restorer",
        [RemoveBackground.Model] = "RMBG-1.4 by BRIA, which finds the subject",
        [Matting.Model] = "ViTMatte, which refines the edges",

        // PB80: the text-to-image models PB79 put in the catalog.
        ["z-image-turbo"] = "Z-Image Turbo",
        ["flux-2-klein-4b"] = "FLUX.2 klein 4B",
        ["qwen-image"] = "Qwen-Image",
    };

    /// <summary>
    /// The models an operation still has to download: none when its primary model is on disk,
    /// else the primary and each missing companion, as the manifest lists them.
    /// </summary>
    /// <param name="operation">The operation.</param>
    /// <param name="library">The models on disk.</param>
    /// <param name="manifest">The manifest downloads come from.</param>
    /// <param name="variant">The variant the operation runs in; FP32, what downloads bring, also counts.</param>
    /// <returns>The models to fetch, empty when the operation can run or the manifest cannot help.</returns>
    public static IReadOnlyList<ManifestModel> Needed(FirstUseOperation operation, ModelLibrary library, ModelManifest manifest, string variant)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(manifest);
        if (Present(library, operation.Primary, variant) || manifest.Model(operation.Primary) is null)
        {
            return [];
        }

        return [.. operation.Companions.Where(m => !Present(library, m, variant)).Prepend(operation.Primary)
            .Select(manifest.Model).OfType<ManifestModel>()];
    }

    /// <summary>A model's name as the consent panel lists it.</summary>
    /// <param name="model">Its folder name.</param>
    /// <returns>A readable name, or the folder name.</returns>
    public static string NameOf(string model) => Names.GetValueOrDefault(model, model);

    /// <summary>Where a file comes from, as people recognise it: host and repository.</summary>
    /// <param name="file">The manifest's entry.</param>
    /// <returns>e.g. huggingface.co/briaai/RMBG-1.4.</returns>
    public static string SourceOf(ManifestFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        Uri url = new(file.Url);
        string[] segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 ? $"{url.Host}/{segments[0]}/{segments[1]}" : url.Host;
    }

    private static bool Present(ModelLibrary library, string model, string variant) =>
        library.Has(model, variant) || library.Has(model, ModelDownloads.Variant);
}

/// <summary>
/// PB58: a failed download as the card says it, what happened and what to do, so a person who
/// lost the connection is told to reconnect rather than shown an exception.
/// </summary>
/// <param name="What">What happened.</param>
/// <param name="Todo">What to do about it, and what was kept.</param>
public sealed record DownloadFailure(string What, string Todo)
{
    private const int DiskFull = unchecked((int)0x80070070);
    private const int HandleDiskFull = unchecked((int)0x80070027);

    /// <summary>Explains a failure <see cref="ModelDownloads.DownloadAsync"/> threw.</summary>
    /// <param name="failure">What it threw.</param>
    /// <param name="kept">Bytes already on disk that the next try keeps.</param>
    /// <returns>The explanation.</returns>
    public static DownloadFailure Explain(Exception failure, long kept)
    {
        ArgumentNullException.ThrowIfNull(failure);
        string keeping = kept >= 1 << 20 ? $" The {kept >> 20} MB already downloaded are kept." : string.Empty;
        return failure switch
        {
            HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError } =>
                new("This PC seems to be offline.", "Connect to the internet and try again." + keeping),
            HttpRequestException { StatusCode: { } missing } when (int)missing is >= 400 and < 500 =>
                new($"The download server no longer has this file (error {(int)missing}).", "Update Revelei, which knows the current download locations, then try again." + keeping),
            HttpRequestException { StatusCode: { } status } =>
                new($"The download server answered with error {(int)status}.", "This is usually temporary: try again in a few minutes." + keeping),
            HttpRequestException =>
                new("The download server could not be reached.", "Check the connection and try again." + keeping),
            InvalidDataException =>
                new("A file arrived damaged, so it was deleted and never used.", "Try again: it is downloaded afresh."),
            UnauthorizedAccessException =>
                new("Revelei is not allowed to write to its models folder.", "Check the folder's permissions, then try again."),
            NotEnoughDiskException disk =>
                new($"The download needs {disk.Needed >> 20} MB and the disk has {disk.Free >> 20} MB free.", "Free some space, then try again." + keeping),
            IOException { HResult: DiskFull or HandleDiskFull } =>
                new("The disk is full.", "Free some space and try again." + keeping),
            IOException =>
                new($"The download was interrupted: {failure.Message}", "Try again." + keeping),
            _ => new(failure.Message, "Try again." + keeping),
        };
    }
}
