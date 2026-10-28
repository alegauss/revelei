namespace Revelei.Inference;

/// <summary>Which cards the Models panel shows.</summary>
public enum CardFilter
{
    /// <summary>Every model the catalog lists, and every withdrawn one still on disk.</summary>
    All,

    /// <summary>Models on this PC.</summary>
    Installed,

    /// <summary>Models not on this PC yet.</summary>
    Available,

    /// <summary>Downloaded models the catalog has an update for.</summary>
    Updates,
}

/// <summary>One model as the Models panel's card shows it (PB75).</summary>
/// <param name="Name">The model's folder name.</param>
/// <param name="Title">The name a person reads.</param>
/// <param name="Group">Upscale, Faces, Background, Denoise or Generate.</param>
/// <param name="License">Its license.</param>
/// <param name="NonCommercial">Whether it carries the non-commercial badge.</param>
/// <param name="Quantisation">The recommended quantisation (PB72), or the one installed.</param>
/// <param name="Verdict">How it would run here, or null where nothing fits.</param>
/// <param name="DownloadBytes">What the recommended quantisation downloads.</param>
/// <param name="DiskBytes">What it takes on disk now, partial downloads included.</param>
/// <param name="Installed">Whether its files are on this PC, downloaded or from the installer.</param>
/// <param name="Bundled">Whether the installer carries it, which a new installer updates, not this panel.</param>
/// <param name="InstalledVersion">The version downloaded, from installed.json, or null.</param>
/// <param name="State">How the download compares with the catalog (PB71), or null when not downloaded.</param>
/// <param name="Note">The catalog's note on what the listed version changed.</param>
/// <param name="Measured">PB76: how fast it ran here, which replaces the estimate once it has run.</param>
public sealed record ModelCard(
    string Name,
    string Title,
    string Group,
    string License,
    bool NonCommercial,
    string? Quantisation,
    FitVerdict? Verdict,
    long DownloadBytes,
    long DiskBytes,
    bool Installed,
    bool Bundled,
    string? InstalledVersion,
    UpdateState? State,
    string? Note,
    Measured? Measured = null)
{
    /// <summary>Whether the card offers Update: a newer download, never for the installer's own.</summary>
    public bool CanUpdate => State == UpdateState.UpdateAvailable && !Bundled;
}

/// <summary>
/// PB75: the cards the Models panel shows, built from the catalog (PB70), what is on disk, the
/// updates (PB71) and the hardware verdicts (PB72), with no window type, so the panel's every
/// line is decided where a test can hold it.
/// </summary>
public static class ModelCards
{
    /// <summary>The groups in the order the panel shows them.</summary>
    public static IReadOnlyList<string> Groups => GroupOrder;

    private static readonly string[] GroupOrder = ["Upscale", "Faces", "Background", "Denoise", "Generate", "Other"];

    /// <summary>Builds every card, grouped and in name order within a group.</summary>
    /// <param name="catalog">The catalog, refreshed or embedded.</param>
    /// <param name="root">The folder downloads go to.</param>
    /// <param name="bundled">The installer's models folder, or null.</param>
    /// <param name="machine">This PC.</param>
    /// <param name="measured">PB76: what has run here, from <see cref="MeasuredSpeed.From"/>; null for none.</param>
    /// <returns>The cards.</returns>
    public static IReadOnlyList<ModelCard> Build(ModelManifest catalog, string root, string? bundled, MachineProfile machine, IReadOnlyList<Measured>? measured = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        List<ModelCard> cards = [.. catalog.Models.Select(model => Card(model, root, bundled, machine, catalog) with
        {
            Measured = MeasuredSpeed.For(measured ?? [], model.Name),
        })];

        // A withdrawn model keeps working and keeps its card, labelled, until it is removed.
        cards.AddRange(ModelUpdates.States(catalog, root)
            .Where(s => s.State == UpdateState.Withdrawn)
            .Select(s => new ModelCard(s.Model, FirstUse.NameOf(s.Model), "Other", string.Empty, false, InstalledRecord.Read(Path.Combine(root, s.Model))?.Quantisation, null, 0, DiskBytes(Path.Combine(root, s.Model)), true, false, InstalledRecord.Read(Path.Combine(root, s.Model))?.Version, UpdateState.Withdrawn, null)));

        return [.. cards.OrderBy(c => Array.IndexOf(GroupOrder, c.Group)).ThenBy(c => c.Title, StringComparer.CurrentCulture)];
    }

    /// <summary>The cards a filter keeps.</summary>
    /// <param name="cards">Every card.</param>
    /// <param name="filter">The filter.</param>
    /// <returns>The cards it keeps, in order.</returns>
    public static IEnumerable<ModelCard> Filter(IEnumerable<ModelCard> cards, CardFilter filter) => filter switch
    {
        CardFilter.Installed => cards.Where(c => c.Installed),
        CardFilter.Available => cards.Where(c => !c.Installed),
        CardFilter.Updates => cards.Where(c => c.CanUpdate),
        _ => cards,
    };

    /// <summary>The group a model belongs to.</summary>
    /// <param name="model">The catalog's model.</param>
    /// <returns>The group.</returns>
    public static string GroupOf(ManifestModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Kind == ModelKind.Diffusion ? "Generate" : model.Task switch
        {
            "upscale" => "Upscale",
            "face-restore" or "face-detection" => "Faces",
            "background-removal" or "matting" => "Background",
            "denoise" => "Denoise",
            _ => "Other",
        };
    }

    private static ModelCard Card(ManifestModel model, string root, string? bundled, MachineProfile machine, ModelManifest catalog)
    {
        string folder = Path.Combine(root, model.Name);
        bool downloaded = File.Exists(Path.Combine(folder, "meta.json"));
        bool fromInstaller = bundled is not null && File.Exists(Path.Combine(bundled, model.Name, "meta.json"));
        InstalledRecord? record = InstalledRecord.Read(folder);
        (string Quantisation, FitVerdict Verdict)? recommended = HardwareFit.Recommend(model, machine);
        string? quantisation = record?.Quantisation ?? recommended?.Quantisation;
        FitVerdict? verdict = quantisation is not null && model.Quantisations?.GetValueOrDefault(quantisation) is { } chosen
            ? HardwareFit.Judge(chosen, machine, installed: downloaded || fromInstaller)
            : null;
        long download = quantisation is not null && model.Quantisations?.GetValueOrDefault(quantisation) is { } listed
            ? listed.Files.Sum(f => f.File.Bytes) + model.Meta.Bytes
            : 0;

        return new ModelCard(
            model.Name,
            FirstUse.NameOf(model.Name),
            GroupOf(model),
            model.License,
            model.NonCommercial,
            quantisation,
            verdict,
            download,
            DiskBytes(folder),
            downloaded || fromInstaller,
            fromInstaller,
            record?.Version,
            downloaded ? ModelUpdates.State(catalog, root, model.Name) : null,
            model.Note);
    }

    private static long DiskBytes(string folder) =>
        Directory.Exists(folder) ? new DirectoryInfo(folder).GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
}
