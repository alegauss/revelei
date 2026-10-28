using System.Text.Json;
using System.Text.Json.Serialization;

namespace Revelei.Inference;

/// <summary>
/// What was downloaded into a model's folder (PB71): installed.json beside its meta.json, so the
/// refreshed catalog (PB70) can be compared with what is on disk.
/// </summary>
/// <param name="Version">The model's version as the catalog listed it.</param>
/// <param name="Quantisation">The quantisation fetched, e.g. fp32.</param>
/// <param name="At">When the download finished.</param>
/// <param name="Files">The SHA-256 of each file fetched, by its name in the folder.</param>
public sealed record InstalledRecord(string Version, string Quantisation, DateTimeOffset At, IReadOnlyDictionary<string, string> Files)
{
    /// <summary>The file's name in a model's folder.</summary>
    public const string FileName = "installed.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true,
    };

    /// <summary>The record in a model's folder, or null when there is none or it does not read.</summary>
    /// <param name="folder">The model's folder.</param>
    /// <returns>The record.</returns>
    public static InstalledRecord? Read(string folder)
    {
        string path = Path.Combine(folder, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<InstalledRecord>(File.ReadAllText(path), Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes the record into a model's folder.</summary>
    /// <param name="folder">The model's folder.</param>
    public void Write(string folder) => File.WriteAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(this, Options));
}

/// <summary>How a downloaded model compares with the catalog.</summary>
public enum UpdateState
{
    /// <summary>On disk as the catalog lists it.</summary>
    UpToDate,

    /// <summary>The catalog lists another version or other files.</summary>
    UpdateAvailable,

    /// <summary>The catalog no longer lists it: it keeps working, labelled as such.</summary>
    Withdrawn,
}

/// <summary>
/// PB71: whether each downloaded model is up to date with the catalog, an update is available,
/// or the catalog withdrew it. Reads installed.json, written by <see cref="ModelDownloads"/>; a
/// model downloaded before there was one is compared by the sizes of its files, which is what
/// every refresh can afford without hashing hundreds of megabytes.
/// </summary>
public static class ModelUpdates
{
    /// <summary>The version a catalog gives a model: its own, or 1 where schema 1 has none.</summary>
    /// <param name="model">The catalog's model.</param>
    /// <returns>The version.</returns>
    public static string VersionOf(ManifestModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.ModelVersion ?? "1";
    }

    /// <summary>How one downloaded model compares with the catalog.</summary>
    /// <param name="catalog">The catalog, refreshed or embedded.</param>
    /// <param name="root">The folder downloads go to.</param>
    /// <param name="model">The model's folder name.</param>
    /// <returns>Its state.</returns>
    public static UpdateState State(ModelManifest catalog, string root, string model)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.Model(model) is not { } listed)
        {
            return UpdateState.Withdrawn;
        }

        string folder = Path.Combine(root, model);
        if (InstalledRecord.Read(folder) is { } record)
        {
            return record.Version == VersionOf(listed) && SameFiles(record, listed) ? UpdateState.UpToDate : UpdateState.UpdateAvailable;
        }

        // Downloaded before installed.json existed: the sizes are what tell.
        bool sized = listed.Meta is { } meta && Sized(Path.Combine(folder, "meta.json"), meta)
            && listed.Variants.Any(v => Sized(Path.Combine(folder, v.Key + ".onnx"), v.Value));
        return sized ? UpdateState.UpToDate : UpdateState.UpdateAvailable;
    }

    /// <summary>Every model in the download folder that has a meta.json, with its state.</summary>
    /// <param name="catalog">The catalog.</param>
    /// <param name="root">The folder downloads go to.</param>
    /// <returns>Each model's name and state, by name.</returns>
    public static IReadOnlyList<(string Model, UpdateState State)> States(ModelManifest catalog, string root) =>
        !Directory.Exists(root)
            ? []
            : [.. Directory.EnumerateDirectories(root)
                .Where(folder => File.Exists(Path.Combine(folder, "meta.json")) || File.Exists(Path.Combine(folder, InstalledRecord.FileName)))
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .Select(name => (name!, State(catalog, root, name!)))];

    /// <summary>
    /// Brings one downloaded model to the catalog's version. The new files are fetched into
    /// &lt;model&gt;/.update/ and verified there, by the same resumable path every download takes;
    /// only then are they moved over the old ones, weights before meta.json and installed.json
    /// last, and the staging folder deleted. A failed, cancelled or interrupted update leaves the
    /// working model as it was, and the next try resumes it. Where only the version changed and
    /// the files did not, the record is rewritten and nothing is fetched.
    /// </summary>
    /// <param name="http">The client.</param>
    /// <param name="catalog">The catalog that lists the update.</param>
    /// <param name="root">The folder downloads go to; the installer's models are a new installer's to update.</param>
    /// <param name="model">The model's folder name.</param>
    /// <param name="progress">Told as <see cref="ModelDownloads.DownloadAsync"/> tells it.</param>
    /// <param name="cancellation">Cancels the fetch; the staged .part files stay for the next try.</param>
    /// <param name="retryDelay">The first pause before a retry.</param>
    /// <returns>How many files were fetched.</returns>
    /// <exception cref="InvalidOperationException">The catalog does not list the model.</exception>
    public static async Task<DownloadResult> UpdateAsync(
        HttpClient http,
        ModelManifest catalog,
        string root,
        string model,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellation,
        TimeSpan? retryDelay = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ManifestModel listed = catalog.Model(model) ?? throw new InvalidOperationException($"The catalog no longer lists {model}, so there is nothing to update it to.");
        string folder = Path.Combine(root, model);
        string staging = Path.Combine(folder, Staging);
        if (InstalledRecord.Read(folder) is { } record && SameFiles(record, listed))
        {
            (record with { Version = VersionOf(listed) }).Write(folder);
            return new DownloadResult(0, 0);
        }

        DownloadResult result = await ModelDownloads.DownloadAsync(http, catalog, staging, progress, cancellation, models: [model], retryDelay: retryDelay).ConfigureAwait(false);

        string staged = Path.Combine(staging, model);
        foreach (string name in (string[])[ModelDownloads.Variant + ".onnx", "meta.json", InstalledRecord.FileName])
        {
            File.Move(Path.Combine(staged, name), Path.Combine(folder, name), overwrite: true);
        }

        Directory.Delete(staging, recursive: true);
        return result;
    }

    /// <summary>The folder inside a model's own where an update is staged until it is verified.</summary>
    public const string Staging = ".update";

    // An ONNX model is its weights and meta.json; a diffusion model (PB79) is its files by role.
    private static bool SameFiles(InstalledRecord record, ManifestModel listed) =>
        (listed.Meta is null || record.Files.GetValueOrDefault("meta.json") == listed.Meta.Sha256)
        && (listed.Kind == ModelKind.Diffusion
            ? listed.Quantisations?.GetValueOrDefault(record.Quantisation) is { } quantisation
                && quantisation.Files.All(p => record.Files.GetValueOrDefault(p.Role) == p.File.Sha256)
            : listed.Variants.TryGetValue(record.Quantisation, out ManifestFile? weights)
                && record.Files.GetValueOrDefault(record.Quantisation + ".onnx") == weights.Sha256);

    private static bool Sized(string path, ManifestFile expected)
    {
        FileInfo file = new(path);
        return file.Exists && file.Length == expected.Bytes;
    }
}
