using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Revelei.Inference;

namespace Revelei.App;

/// <summary>
/// PB58: face restore and background removal fetch their models the first time they are used.
/// The card says what the one-time download is before anything happens; its button opens a
/// consent panel with every file, the total, the source, the folder and the license; and once
/// agreed the download runs beside the app, every other operation still working, with megabytes,
/// speed and time left on the card and in the status bar. It can be paused and resumed, a failure
/// says what to do, and the operation starts by itself once its models are verified.
/// </summary>
public partial class MainWindow
{
    // One download at a time, the About panel's included, so two never write the same file.
    private static readonly SemaphoreSlim DownloadGate = new(1, 1);

    private FirstUseCard? faceFirstUse;
    private FirstUseCard? backgroundFirstUse;
    private FirstUseCard? consenting;
    private Action? pendingRun;

    private enum FirstUseStage
    {
        Offered,
        Waiting,
        Downloading,
        Paused,
        Failed,
    }

    private IEnumerable<FirstUseCard> FirstUseCards => new[] { faceFirstUse, backgroundFirstUse }.OfType<FirstUseCard>();

    /// <summary>Whether a first-use download is running or waiting its turn.</summary>
    private bool FirstUseDownloading => FirstUseCards.Any(card => card.Downloading is not null);

    /// <summary>The folders other than the download folder, whose identical files are not fetched again.</summary>
    private IReadOnlyList<string> Elsewhere => [.. Options.ModelFolders.Where(folder => folder != Options.ModelsFolder)];

    private void StartFirstUse()
    {
        faceFirstUse = new(FirstUse.Faces, FaceFirstUse, FaceFirstUseBadge, FaceFirstUseDetail, FaceFirstUseProgress, FaceFirstUseAction, FaceLicence,
            () => OnRestoreFacesClick(this, new RoutedEventArgs()));
        backgroundFirstUse = new(FirstUse.Background, BackgroundFirstUse, BackgroundFirstUseBadge, BackgroundFirstUseDetail, BackgroundFirstUseProgress, BackgroundFirstUseAction, BackgroundLicence,
            () => OnRemoveBackgroundClick(this, new RoutedEventArgs()));
        Closed += (_, _) =>
        {
            // The .part files stay, so the next start resumes them.
            foreach (FirstUseCard card in FirstUseCards)
            {
                card.Downloading?.Cancel();
            }
        };
    }

    /// <summary>
    /// Works out what the card's operation still has to download, and shows it: the offer, the
    /// download's progress, a pause or a failure, or that it is ready. Returns whether the
    /// operation's button should offer the download rather than run.
    /// </summary>
    /// <param name="card">The card.</param>
    /// <param name="variant">The variant the operation runs in.</param>
    /// <param name="blocked">Another model, one no download brings, is missing, so the card says that instead.</param>
    /// <returns>True when the operation needs its download first.</returns>
    private bool RefreshFirstUse(FirstUseCard card, string variant, bool blocked)
    {
        card.Needed = FirstUse.Needed(card.Operation, library!, Manifest, variant);
        bool fetching = card.Needed.Count > 0;
        card.Box.Visibility = !blocked && (fetching || card.Ready is not null) ? Visibility.Visible : Visibility.Collapsed;
        card.Bar.Visibility = Visibility.Collapsed;
        card.Action.Visibility = Visibility.Collapsed;
        card.Badge.Parent.SetValue(VisibilityProperty, Visibility.Visible);
        if (!fetching)
        {
            if (card.AutoRunning && !Busy && pendingRun is null)
            {
                card.AutoRunning = false;
                card.Ready = $"Ready: {card.Operation.Title} now works offline.";
            }

            card.Stage = FirstUseStage.Offered;
            card.Badge.Parent.SetValue(VisibilityProperty, Visibility.Collapsed);
            card.Detail.Text = card.Ready ?? string.Empty;
            return false;
        }

        card.Ready = null;
        string[] names = Names(card);
        long missing = ModelDownloads.PendingBytes(Manifest, Options.ModelsFolder, Elsewhere, names);
        long total = ModelDownloads.TotalBytes(Manifest, names);
        switch (card.Stage)
        {
            case FirstUseStage.Waiting:
                card.Badge.Text = "Download queued";
                card.Detail.Text = "It starts as soon as the other download finishes.";
                ShowAction(card, "Cancel");
                break;
            case FirstUseStage.Downloading:
                DownloadProgress progress = card.Last ?? new(string.Empty, total - missing, total);
                card.Badge.Text = $"Downloading · {DownloadText.Percent(progress)}%";
                card.Detail.Text = DownloadText.Line(progress);
                card.Bar.Value = DownloadText.Percent(progress);
                card.Bar.Visibility = Visibility.Visible;
                ShowAction(card, "Pause");
                break;
            case FirstUseStage.Paused:
                card.Badge.Text = $"Paused · {DownloadText.Percent(new DownloadProgress(string.Empty, total - missing, total))}%";
                card.Detail.Text = "Resume picks up where it stopped; nothing already downloaded is fetched again.";
                card.Bar.Value = total == 0 ? 0 : 100 * (total - missing) / total;
                card.Bar.Visibility = Visibility.Visible;
                ShowAction(card, "Resume download");
                break;
            case FirstUseStage.Failed:
                card.Badge.Text = "Download stopped";
                card.Detail.Text = $"{card.Failure!.What} {card.Failure.Todo}";
                ShowAction(card, "Try again");
                break;
            default:
                card.Badge.Text = missing < total
                    ? $"One-time download · {DownloadText.Size(missing)} left"
                    : $"One-time download · {DownloadText.Size(total)}";
                card.Detail.Text = $"From {Sources(card)}. Your photo is never uploaded, and after this {card.Operation.Title} works offline.";
                break;
        }

        return true;
    }

    private static void ShowAction(FirstUseCard card, string label)
    {
        card.Action.Content = label;
        card.Action.Tag = card;
        card.Action.Visibility = Visibility.Visible;
    }

    /// <summary>Offers the download in the consent panel, which says everything before anything is fetched.</summary>
    private void ShowConsent(FirstUseCard card)
    {
        consenting = card;
        string[] names = Names(card);
        long total = ModelDownloads.TotalBytes(Manifest, names);
        long missing = ModelDownloads.PendingBytes(Manifest, Options.ModelsFolder, Elsewhere, names);
        string verb = card.Operation.Verb;

        ConsentTitle.Text = $"{card.Operation.Title} needs a one-time download";
        ConsentLead.Text = $"To keep the installer small, {card.Operation.Title} downloads its AI {(card.Needed.Count == 1 ? "model" : "models")} the first time you use it: "
            + $"{DownloadText.Size(missing)}. After that it works offline, for every photo you open.";
        ConsentFiles.ItemsSource = card.Needed
            .Select(m => $"{FirstUse.NameOf(m.Name)} · {DownloadText.Size(m.Meta.Bytes + m.Variants[ModelDownloads.Variant].Bytes)} · from {FirstUse.SourceOf(m.Variants[ModelDownloads.Variant])}")
            .ToList();
        ConsentTotal.Text = (missing < total
            ? $"Total: {DownloadText.Size(missing)}, since {DownloadText.Size(total - missing)} of it is already here."
            : $"Total: {DownloadText.Size(total)}.") + FreeSpace(missing);
        ConsentFacts.Text = string.Join(Environment.NewLine,
            "• Only the models are downloaded. Your photo stays on this PC and is never uploaded.",
            $"• They are saved in {Options.ModelsFolder}, and the Models panel can remove them at any time.",
            "• Each file is checked against its published SHA-256 before it is used; a damaged one is deleted.",
            "• Revelei keeps working while it downloads. You can pause it, and it resumes where it stopped.",
            $"• Once the download is verified, {card.Operation.Title} starts on this photo by itself.");
        ConsentLicense.Text = string.Join(Environment.NewLine,
            card.Needed.Select(m => $"{FirstUse.NameOf(m.Name)}: {m.License}.").Append(card.Licence.Text));
        ConsentAccept.Content = $"Download and {verb}";

        if (AboutPanel.Visibility == Visibility.Visible)
        {
            OnAboutClick(this, new RoutedEventArgs());
        }

        if (ModelsPanel.Visibility == Visibility.Visible)
        {
            OnModelsClick(this, new RoutedEventArgs());
        }

        ConsentPanel.Visibility = Visibility.Visible;
        Stage.Visibility = Visibility.Collapsed;
        ConsentAccept.Focus();
    }

    private string FreeSpace(long needed)
    {
        try
        {
            DriveInfo drive = new(Path.GetPathRoot(Path.GetFullPath(Options.ModelsFolder))!);
            long free = drive.AvailableFreeSpace;
            return free < needed + (needed / 10)
                ? $" There is only {DownloadText.Size(free)} free on {drive.Name.TrimEnd('\\')}: free some space first."
                : $" {DownloadText.Size(free)} is free on {drive.Name.TrimEnd('\\')}.";
        }
        catch (Exception unknown) when (unknown is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return string.Empty; // A network folder or a drive Windows will not describe: nothing to warn about.
        }
    }

    private void HideConsent()
    {
        consenting = null;
        ConsentPanel.Visibility = Visibility.Collapsed;
        Stage.Visibility = Visibility.Visible;
    }

    private void OnConsentAccept(object sender, RoutedEventArgs e)
    {
        if (consenting is not { } card)
        {
            return;
        }

        HideConsent();
        card.RunWhenReady = true;
        _ = DownloadFirstUseAsync(card);
    }

    private void OnConsentDecline(object sender, RoutedEventArgs e) => HideConsent();

    private void OnFirstUseActionClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not FirstUseCard card)
        {
            return;
        }

        if (card.Downloading is { } inFlight)
        {
            inFlight.Cancel();
        }
        else
        {
            _ = DownloadFirstUseAsync(card);
        }
    }

    /// <summary>Fetches the card's models, then runs its operation when it was asked for.</summary>
    private async Task DownloadFirstUseAsync(FirstUseCard card)
    {
        string[] names = Names(card);
        card.Downloading = new CancellationTokenSource();
        card.Failure = null;
        card.Last = null;
        CancellationToken token = card.Downloading.Token;
        bool entered = false;
        try
        {
            card.Stage = FirstUseStage.Waiting;
            RefreshOperations();
            await DownloadGate.WaitAsync(token).ConfigureAwait(true);
            entered = true;
            card.Stage = FirstUseStage.Downloading;
            RefreshOperations();
            Progress<DownloadProgress> progress = new(p =>
            {
                if (card.Stage != FirstUseStage.Downloading)
                {
                    return; // A report that arrived after the download ended.
                }

                // A report per megabyte is more than a person reads; four a second is enough.
                bool turned = card.Last?.Stage != p.Stage;
                card.Last = p;
                if (!turned && card.Shown.IsRunning && card.Shown.ElapsedMilliseconds < 250)
                {
                    return;
                }

                card.Shown.Restart();
                RefreshOperations();
                DownloadChipText.Text = $"{card.Operation.Title}: {DownloadText.Line(p)}";
                DownloadChip.Visibility = Visibility.Visible;
            });
            await ModelDownloads.DownloadAsync(Http, Manifest, Options.ModelsFolder, progress, token, Elsewhere, names).ConfigureAwait(true);
            card.Stage = FirstUseStage.Offered;
            card.Ready = $"Ready: {card.Operation.Title} now works offline.";
            if (card.RunWhenReady)
            {
                // Said on the card, since the button it greys out is where the person is looking.
                card.RunWhenReady = false;
                card.AutoRunning = true;
                card.Ready = $"Downloaded and verified. {card.Operation.Title} is running on this photo; its progress is at the top.";
                pendingRun = card.Run;
            }
        }
        catch (OperationCanceledException)
        {
            card.Stage = entered ? FirstUseStage.Paused : FirstUseStage.Offered;
        }
        catch (Exception failure) when (failure is HttpRequestException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            long kept = ModelDownloads.TotalBytes(Manifest, names) - ModelDownloads.PendingBytes(Manifest, Options.ModelsFolder, Elsewhere, names);
            card.Failure = DownloadFailure.Explain(failure, kept);
            card.Stage = FirstUseStage.Failed;
        }
        finally
        {
            if (entered)
            {
                DownloadGate.Release();
            }

            card.Downloading.Dispose();
            card.Downloading = null;
            DownloadChip.Visibility = FirstUseDownloading ? DownloadChip.Visibility : Visibility.Collapsed;
            if (AboutPanel.Visibility == Visibility.Visible)
            {
                FillAbout();
            }

            RefreshOperations();
        }
    }

    /// <summary>
    /// Runs the operation whose download just finished, once nothing else is running: the person
    /// asked for it when they agreed to the download, so they are not asked again.
    /// </summary>
    private void RunPending()
    {
        if (pendingRun is not { } run || Busy)
        {
            return;
        }

        pendingRun = null;
        if (Image is not null)
        {
            Dispatcher.BeginInvoke(run);
        }
    }

    private static string[] Names(FirstUseCard card) => [.. card.Needed.Select(m => m.Name)];

    private static string Sources(FirstUseCard card) =>
        string.Join(" and ", card.Needed.Select(m => FirstUse.SourceOf(m.Variants[ModelDownloads.Variant])).Distinct(StringComparer.Ordinal));

    /// <summary>One card's first-use controls and where its download stands.</summary>
    private sealed class FirstUseCard(FirstUseOperation operation, FrameworkElement box, TextBlock badge, TextBlock detail, ProgressBar bar, Button action, TextBlock licence, Action run)
    {
        public FirstUseOperation Operation { get; } = operation;

        public FrameworkElement Box { get; } = box;

        public TextBlock Badge { get; } = badge;

        public TextBlock Detail { get; } = detail;

        public ProgressBar Bar { get; } = bar;

        public Button Action { get; } = action;

        public TextBlock Licence { get; } = licence;

        public Action Run { get; } = run;

        public IReadOnlyList<ManifestModel> Needed { get; set; } = [];

        public FirstUseStage Stage { get; set; }

        public CancellationTokenSource? Downloading { get; set; }

        public DownloadProgress? Last { get; set; }

        public DownloadFailure? Failure { get; set; }

        public string? Ready { get; set; }

        public bool RunWhenReady { get; set; }

        public bool AutoRunning { get; set; }

        public System.Diagnostics.Stopwatch Shown { get; } = new();
    }
}
