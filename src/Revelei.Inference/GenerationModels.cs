namespace Revelei.Inference;

/// <summary>A text-to-image model on this PC, with the files the engine loads (PB80).</summary>
/// <param name="Name">Its folder name, e.g. z-image-turbo.</param>
/// <param name="Title">The name a person reads.</param>
/// <param name="Quantisation">The quantisation installed.</param>
/// <param name="Transformer">The diffusion model's file.</param>
/// <param name="TextEncoder">The text encoder's file.</param>
/// <param name="Vae">The VAE's file.</param>
/// <param name="Defaults">The steps and guidance it was made for.</param>
/// <param name="Verdict">How it runs here (PB72), or null where the catalog no longer lists it.</param>
public sealed record GenerationModel(
    string Name,
    string Title,
    string Quantisation,
    string Transformer,
    string TextEncoder,
    string Vae,
    ModelDefaults Defaults,
    FitVerdict? Verdict);

/// <summary>
/// PB80: the generation models the Create screen offers: every downloaded model whose
/// installed.json names a transformer, a text encoder and a VAE, each present in the shared
/// folder (PB73). The catalog adds the title, the defaults and the verdict; a model it no longer
/// lists still generates, with the defaults most distilled models take.
/// </summary>
public static class GenerationModels
{
    /// <summary>The defaults for a model the catalog does not describe.</summary>
    public static readonly ModelDefaults Fallback = new(8, 1);

    /// <summary>The installed generation models, by name.</summary>
    /// <param name="root">The folder downloads go to.</param>
    /// <param name="catalog">The catalog.</param>
    /// <param name="machine">This PC, for the verdict.</param>
    /// <returns>The models.</returns>
    public static IReadOnlyList<GenerationModel> Installed(string root, ModelManifest catalog, MachineProfile machine)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!Directory.Exists(root))
        {
            return [];
        }

        string shared = Path.Combine(root, ModelDownloads.SharedFolder);
        List<GenerationModel> found = [];
        foreach (string folder in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            if (InstalledRecord.Read(folder) is not { } record
                || !record.Files.TryGetValue("transformer", out string? transformer)
                || !record.Files.TryGetValue("text-encoder", out string? encoder)
                || !record.Files.TryGetValue("vae", out string? vae))
            {
                continue;
            }

            string[] paths = [Path.Combine(shared, transformer), Path.Combine(shared, encoder), Path.Combine(shared, vae)];
            if (!paths.All(File.Exists))
            {
                continue; // A file went missing, or a removal is under way: offering it would fail at load.
            }

            string name = Path.GetFileName(folder);
            ManifestModel? listed = catalog.Model(name);
            FitVerdict? verdict = listed?.Quantisations?.GetValueOrDefault(record.Quantisation) is { } quantisation
                ? HardwareFit.Judge(quantisation, machine, installed: true)
                : null;
            found.Add(new GenerationModel(name, FirstUse.NameOf(name), record.Quantisation, paths[0], paths[1], paths[2], listed?.Defaults ?? Fallback, verdict));
        }

        return found;
    }
}
