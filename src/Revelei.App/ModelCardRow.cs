using System.ComponentModel;
using System.Windows;
using Revelei.Inference;

namespace Revelei.App;

/// <summary>
/// One card of the Models panel (PB75): a <see cref="ModelCard"/> as the window binds it, with
/// where it stands in the download queue (PB73). Only <see cref="Status"/> changes while a card
/// is shown, so a download's progress updates one line rather than rebuilding the list.
/// </summary>
/// <param name="card">The card.</param>
/// <param name="queued">Its place in the queue, or null when it is not queued.</param>
/// <param name="running">Whether it is the one downloading now.</param>
public sealed class ModelCardRow(ModelCard card, QueuedDownload? queued, bool running) : INotifyPropertyChanged
{
    private string? progress;

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The card it shows.</summary>
    public ModelCard Card { get; } = card;

    /// <summary>The model's folder name, which every button carries as its tag.</summary>
    public string Name => Card.Name;

    /// <summary>The name a person reads.</summary>
    public string Title => Card.Title;

    /// <summary>The group, the size and the license, with the non-commercial badge where it applies.</summary>
    public string Detail => string.Join(" · ", new[]
    {
        Card.Group,
        Card.Installed ? $"{DownloadText.Size(Card.DiskBytes)} on disk" : $"{DownloadText.Size(Card.DownloadBytes)} to download",
        Card.Quantisation,
        string.IsNullOrEmpty(Card.License) ? null : Card.License,
        Card.NonCommercial ? "non-commercial" : null,
    }.Where(part => !string.IsNullOrEmpty(part)));

    /// <summary>How it ran here (PB76), or failing that how it would (PB72), in words.</summary>
    public string Fit => Card switch
    {
        { Measured: { } measured } => MeasuredSpeed.Line(measured),
        { Verdict: { } verdict } => $"{Words(verdict.Fit)}: {verdict.Reason}",
        _ => "Does not fit this PC.",
    };

    /// <summary>Installed, its version, its update, or where it is in the queue.</summary>
    public string Status => progress ?? (queued, running) switch
    {
        ({ Failure: { } failure }, _) => $"Stopped: {failure}",
        ({ Paused: true }, _) => "Paused. What was downloaded is kept.",
        (not null, true) => "Downloading…",
        (not null, false) => "Queued.",
        _ => Card switch
        {
            { State: UpdateState.Withdrawn } => $"Withdrawn from the catalog. It keeps working{Version}.",
            { CanUpdate: true } => $"Update available{Version}{(Card.Note is { } note ? $": {note}" : ".")}",
            { Bundled: true } => "Installed with Revelei; a new version of Revelei updates it.",
            { Installed: true } => $"Installed{Version}.",
            _ => "Not on this PC.",
        },
    };

    /// <summary>Download, or Update where the catalog has a newer one.</summary>
    public string DownloadLabel => Card.CanUpdate ? "Update" : "Download";

    /// <summary>Pause, or Resume on a paused item.</summary>
    public string PauseLabel => queued is { Paused: true } ? "Resume" : "Pause";

    /// <summary>Download or Update shows when there is something to fetch and it is not queued.</summary>
    public Visibility DownloadVisibility => queued is null && (!Card.Installed || Card.CanUpdate) && Card.Verdict is not null && Card.State != UpdateState.Withdrawn
        ? Visibility.Visible
        : Visibility.Collapsed;

    /// <summary>Pause and Cancel show while it is queued.</summary>
    public Visibility QueuedVisibility => queued is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Remove shows on a download, never on the installer's own models.</summary>
    public Visibility RemoveVisibility => queued is null && Card.Installed && !Card.Bundled ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Automation ids, unique per model.</summary>
    public string TitleId => $"ModelTitle-{Name}";

    /// <summary>The detail line's automation id.</summary>
    public string DetailId => $"ModelDetail-{Name}";

    /// <summary>The verdict line's automation id.</summary>
    public string FitId => $"ModelFit-{Name}";

    /// <summary>The status line's automation id.</summary>
    public string StatusId => $"ModelStatus-{Name}";

    /// <summary>The Download or Update button's automation id.</summary>
    public string DownloadId => $"Download-{Name}";

    /// <summary>The Download or Update button's accessible name.</summary>
    public string DownloadName => $"{DownloadLabel} {Name}";

    /// <summary>The Pause or Resume button's automation id.</summary>
    public string PauseId => $"Pause-{Name}";

    /// <summary>The Cancel button's automation id.</summary>
    public string CancelId => $"Cancel-{Name}";

    /// <summary>The Remove button's automation id.</summary>
    public string RemoveId => $"Remove-{Name}";

    /// <summary>The Remove button's accessible name.</summary>
    public string RemoveName => $"Remove {Name}";

    private string Version => Card.InstalledVersion is { } version ? $", version {version}" : string.Empty;

    /// <summary>Shows a download's progress in place of the status, or the status again with null.</summary>
    /// <param name="line">The progress line.</param>
    public void ShowProgress(string? line)
    {
        progress = line;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
    }

    private static string Words(Fit fit) => fit switch
    {
        Inference.Fit.RunsWell => "Runs well",
        Inference.Fit.RunsSlower => "Runs, slower",
        Inference.Fit.CpuOnly => "CPU only, very slow",
        _ => "Does not fit",
    };
}
