using System.IO;
using System.Net.Http;
using System.Windows;
using Revelei.Inference;

namespace Revelei.App;

/// <summary>A downloaded model as About lists it (PB58), with what it takes on disk.</summary>
/// <param name="Name">Its folder name.</param>
/// <param name="Label">What the row says.</param>
public sealed record DownloadedModel(string Name, string Label)
{
    /// <summary>The Remove button's automation id.</summary>
    public string RemoveId => $"Remove-{Name}";

    /// <summary>The Remove button's accessible name.</summary>
    public string RemoveName => $"Remove {Name}";
}

/// <summary>
/// The Downloads block of the About panel (PB13, PB58): every model downloaded so far with the
/// disk it takes, each removable, and every model the manifest lists at once, the optional FP32
/// set included, for working offline. Each file is checked against its SHA-256 before anything
/// loads it, and the installer's models keep working whether or not this ever runs.
/// </summary>
public partial class MainWindow
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private CancellationTokenSource? downloading;

    private ModelManifest? manifest;

    /// <summary>
    /// The manifest downloads come from: the pinned one this build embeds, or PB59's --manifest
    /// file, whose plain http may only reach this machine's loopback.
    /// </summary>
    private ModelManifest Manifest => manifest ??= Options.Manifest is { } file
        ? ModelManifest.Parse(File.ReadAllText(file), loopback: true)
        : ModelDownloads.Embedded;

    /// <summary>Says what is downloaded and what is left, and lists the downloads.</summary>
    private void RefreshDownloads()
    {
        long left = ModelDownloads.PendingBytes(Manifest, Options.ModelsFolder, Elsewhere);
        string cpu = Provider?.Provider == ExecutionProvider.Cpu
            ? " On this PC's CPU the high-quality ones are also about twice as fast."
            : string.Empty;
        DownloadNote.Text = left == 0
            ? "Every model is on this PC, so every operation works offline."
            : "Restore faces and Remove background download their models the first time you use them. "
                + $"To work offline from the start, download everything now: {DownloadText.Size(left)}, the high-quality (FP32) models included.{cpu}";
        DownloadModelsButton.IsEnabled = downloading is null && left > 0;

        List<DownloadedModel> rows = [.. Downloaded()];
        DownloadedModels.ItemsSource = rows;
        DownloadedModels.IsEnabled = !Busy && downloading is null && !FirstUseDownloading;
        DownloadedModels.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The model folders in the download folder, partly downloaded ones included, so the disk they take is never hidden.</summary>
    private IEnumerable<DownloadedModel> Downloaded()
    {
        if (!Directory.Exists(Options.ModelsFolder) || Options.ModelFolders.Count == 1)
        {
            // With --models alone there is no download folder of its own to tidy: it is the only one.
            yield break;
        }

        foreach (DirectoryInfo folder in new DirectoryInfo(Options.ModelsFolder).EnumerateDirectories().OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            FileInfo[] files = folder.GetFiles("*", SearchOption.AllDirectories);
            long bytes = files.Sum(f => f.Length);
            bool partial = files.Any(f => f.Name.EndsWith(".part", StringComparison.Ordinal));
            yield return new(folder.Name, $"{folder.Name} · {DownloadText.Size(bytes)}{(partial ? ", partly downloaded" : string.Empty)}");
        }
    }

    /// <summary>Whether every installed model has its FP32 variant on disk.</summary>
    private bool HighQualityInstalled()
    {
        if (library is not null)
        {
            return Complete(library.Installed());
        }

        using ModelLibrary reading = new(Options.ModelFolders, new ProviderSelection(ExecutionProvider.Cpu, null, null));
        return Complete(reading.Installed());

        static bool Complete(IReadOnlyList<InstalledModel> installed) =>
            installed.Count > 0 && installed.Where(m => m.Meta.Variants.ContainsKey(ModelDownloads.Variant)).All(m => m.Variants.Contains(ModelDownloads.Variant));
    }

    private async void OnDownloadModelsClick(object sender, RoutedEventArgs e)
    {
        downloading = new CancellationTokenSource();
        DownloadModelsButton.IsEnabled = false;
        CancelDownloadButton.Visibility = Visibility.Visible;
        DownloadProgressBar.Value = 0;
        DownloadProgressBar.Visibility = Visibility.Visible;
        bool entered = false;
        try
        {
            if (!DownloadGate.Wait(0))
            {
                DownloadStatus.Text = "Waiting for the download a card started to finish…";
                await DownloadGate.WaitAsync(downloading.Token).ConfigureAwait(true);
            }

            entered = true;
            DownloadStatus.Text = "Connecting…";
            Progress<DownloadProgress> progress = new(p =>
            {
                DownloadProgressBar.Value = p.Total == 0 ? 0 : (double)p.Done / p.Total;
                DownloadStatus.Text = DownloadText.Line(p);
            });
            DownloadResult result = await ModelDownloads.DownloadAsync(Http, Manifest, Options.ModelsFolder, progress, downloading.Token, Elsewhere).ConfigureAwait(true);
            DownloadStatus.Text = result.Fetched == 0
                ? "Nothing to download: every model was already here."
                : $"Downloaded and verified {result.Fetched} file(s). Every operation now works offline.";
        }
        catch (OperationCanceledException)
        {
            DownloadStatus.Text = "Paused. What was downloaded is kept, and the next download goes on from there.";
        }
        catch (Exception failure) when (failure is HttpRequestException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            long kept = ModelDownloads.TotalBytes(Manifest) - ModelDownloads.PendingBytes(Manifest, Options.ModelsFolder, Elsewhere);
            DownloadFailure explained = DownloadFailure.Explain(failure, kept);
            DownloadStatus.Text = $"{explained.What} {explained.Todo}";
        }
        finally
        {
            if (entered)
            {
                DownloadGate.Release();
            }

            downloading.Dispose();
            downloading = null;
            CancelDownloadButton.Visibility = Visibility.Collapsed;
            DownloadProgressBar.Visibility = Visibility.Collapsed;
            FillAbout();
            RefreshOperations();
        }
    }

    private void OnCancelDownloadClick(object sender, RoutedEventArgs e) => downloading?.Cancel();

    /// <summary>Deletes one downloaded model's folder; its card offers the download again.</summary>
    private void OnRemoveModelClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not string name)
        {
            return;
        }

        string folder = Path.Combine(Options.ModelsFolder, name);
        long bytes = Directory.Exists(folder) ? new DirectoryInfo(folder).GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
        try
        {
            Directory.Delete(folder, recursive: true);
            DownloadStatus.Text = $"Removed {name}, freeing {DownloadText.Size(bytes)}. The operation that uses it offers the download again.";
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            DownloadStatus.Text = $"{name} could not be removed while it is in use: {failure.Message} Close Revelei and delete {folder}.";
        }

        FillAbout();
        RefreshOperations();
    }
}
