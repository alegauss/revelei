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
            bool same = record.Version == VersionOf(listed)
                && listed.Variants.TryGetValue(record.Quantisation, out ManifestFile? weights)
                && record.Files.GetValueOrDefault(record.Quantisation + ".onnx") == weights.Sha256
                && record.Files.GetValueOrDefault("meta.json") == listed.Meta.Sha256;
            return same ? UpdateState.UpToDate : UpdateState.UpdateAvailable;
        }

        // Downloaded before installed.json existed: the sizes are what tell.
        bool sized = Sized(Path.Combine(folder, "meta.json"), listed.Meta)
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
                .Where(folder => File.Exists(Path.Combine(folder, "meta.json")))
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .Select(name => (name!, State(catalog, root, name!)))];

    private static bool Sized(string path, ManifestFile expected)
    {
        FileInfo file = new(path);
        return file.Exists && file.Length == expected.Bytes;
    }
}
