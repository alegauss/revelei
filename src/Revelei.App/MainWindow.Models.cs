using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Win32;
using Revelei.Inference;

namespace Revelei.App;

/// <summary>
/// The Models panel (PB75): every catalog model as a card with its group, size, license, the
/// hardware verdict (PB72), its version and update (PB71), and Download, Update, Pause, Cancel
/// and Remove, every download going through the one queue (PB73). The footer says where the
/// models live and moves them (PB74). It takes the stage's place, as About does.
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<string, ModelCardRow> shownCards = new(StringComparer.Ordinal);

    private DownloadQueue? queue;

    private MachineProfile? machine;

    private bool queueRunning;

    private DownloadQueue Queue => queue ??= new DownloadQueue(Path.Combine(Options.ModelsFolder, "queue.json"));

    private void OnModelsClick(object sender, RoutedEventArgs e)
    {
        bool opening = ModelsPanel.Visibility != Visibility.Visible;
        if (opening)
        {
            if (AboutPanel.Visibility == Visibility.Visible)
            {
                OnAboutClick(this, new RoutedEventArgs());
            }

            ConsentPanel.Visibility = Visibility.Collapsed;
            consenting = null;
            FillModels();
        }

        ModelsPanel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
        Stage.Visibility = opening ? Visibility.Collapsed : Visibility.Visible;
        ModelsButton.Content = opening ? "Back" : "Models";
    }

    /// <summary>Rebuilds the cards, the location and the everything-at-once block.</summary>
    private void FillModels()
    {
        machine ??= MachineProfile.Measure(Options.ModelsFolder);
        string? bundled = Options.Models is null ? AppOptions.BundledModels : null;
        IReadOnlyList<ModelCard> cards = ModelCards.Build(Manifest, Options.ModelsFolder, bundled, machine, MeasuredSpeed.From(log.ReadCurrent()));
        IReadOnlyList<QueuedDownload> waiting = Queue.Items;
        string? running = Queue.Running;
        CardFilter filter = ModelsFilterInstalled.IsChecked == true ? CardFilter.Installed
            : ModelsFilterAvailable.IsChecked == true ? CardFilter.Available
            : ModelsFilterUpdates.IsChecked == true ? CardFilter.Updates
            : CardFilter.All;

        shownCards.Clear();
        List<ModelCardRow> rows = [];
        foreach (ModelCard card in ModelCards.Filter(cards, filter))
        {
            ModelCardRow row = new(card, waiting.FirstOrDefault(q => q.Model == card.Name), running == card.Name);
            shownCards[card.Name] = row;
            rows.Add(row);
        }

        ModelCardList.ItemsSource = rows;
        ModelsLocation.Text = Location();
        ModelsChangeFolder.IsEnabled = Options.Models is null; // --models is the tests' own folder.
        RefreshDownloads();
    }

    private string Location()
    {
        try
        {
            DriveInfo drive = new(Path.GetPathRoot(Path.GetFullPath(Options.ModelsFolder))!);
            return $"Downloads go to {Options.ModelsFolder} · {DownloadText.Size(drive.AvailableFreeSpace)} free on {drive.Name.TrimEnd('\\')}.";
        }
        catch (Exception unknown) when (unknown is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return $"Downloads go to {Options.ModelsFolder}.";
        }
    }

    private void OnModelsFilterChecked(object sender, RoutedEventArgs e)
    {
        if (ModelsPanel is not null && ModelsPanel.Visibility == Visibility.Visible)
        {
            FillModels();
        }
    }

    private void OnCardDownloadClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is string name && shownCards.GetValueOrDefault(name)?.Card.Quantisation is { } quantisation)
        {
            Queue.Enqueue(name, quantisation);
            FillModels();
            RunQueue();
        }
    }

    private void OnCardPauseClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not string name)
        {
            return;
        }

        if (Queue.Items.FirstOrDefault(q => q.Model == name) is { Paused: true })
        {
            Queue.Resume(name);
            RunQueue();
        }
        else
        {
            Queue.Pause(name);
        }

        FillModels();
    }

    private void OnCardCancelClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is string name)
        {
            Queue.Cancel(name);
            FillModels();
        }
    }

    /// <summary>Starts the queue unless it is already going; each model waits for the one download gate.</summary>
    private async void RunQueue()
    {
        if (queueRunning)
        {
            return;
        }

        queueRunning = true;
        try
        {
            await Queue.RunAsync(DownloadQueuedAsync, CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            queueRunning = false;
            if (ModelsPanel.Visibility == Visibility.Visible)
            {
                FillModels();
            }

            RefreshOperations();
        }
    }

    private async Task DownloadQueuedAsync(QueuedDownload item, CancellationToken token)
    {
        await Dispatcher.InvokeAsync(FillModels);
        await DownloadGate.WaitAsync(token).ConfigureAwait(true);
        try
        {
            Progress<DownloadProgress> progress = new(p => shownCards.GetValueOrDefault(item.Model)?.ShowProgress(DownloadText.Line(p)));
            ManifestModel model = Manifest.Model(item.Model) ?? throw new InvalidDataException($"The catalog no longer lists {item.Model}.");
            if (model.Kind == ModelKind.Diffusion)
            {
                await ModelDownloads.DownloadPartsAsync(Http, Manifest, Options.ModelsFolder, item.Model, item.Quantisation, progress, token).ConfigureAwait(true);
            }
            else if (ModelUpdates.State(Manifest, Options.ModelsFolder, item.Model) == UpdateState.UpdateAvailable && Directory.Exists(Path.Combine(Options.ModelsFolder, item.Model)))
            {
                await ModelUpdates.UpdateAsync(Http, Manifest, Options.ModelsFolder, item.Model, progress, token).ConfigureAwait(true);
            }
            else
            {
                await ModelDownloads.DownloadAsync(Http, Manifest, Options.ModelsFolder, progress, token, Elsewhere, [item.Model]).ConfigureAwait(true);
            }
        }
        catch (HttpRequestException failure)
        {
            DownloadFailure explained = DownloadFailure.Explain(failure, 0);
            throw new IOException($"{explained.What} {explained.Todo}", failure);
        }
        finally
        {
            DownloadGate.Release();
        }
    }

    /// <summary>PB74: moves the downloads to a folder the person picks, then restarts on it.</summary>
    private async void OnModelsChangeFolderClick(object sender, RoutedEventArgs e)
    {
        OpenFolderDialog dialog = new() { Title = "Where should Revelei keep downloaded models?" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        string chosen = Path.Combine(dialog.FolderName, "Revelei models");
        ModelsChangeFolder.IsEnabled = false;
        try
        {
            Progress<(long Done, long Total)> progress = new(p => DownloadStatus.Text = $"Moving the models: {DownloadText.Size(p.Done)} of {DownloadText.Size(p.Total)}…");
            await ModelsFolderMove.MoveAsync(Options.ModelsFolder, chosen, progress, CancellationToken.None).ConfigureAwait(true);
            new AppSettings(chosen).Save(AppSettings.DefaultPath);
            Restart();
        }
        catch (Exception failure) when (failure is IOException or ArgumentException or UnauthorizedAccessException)
        {
            DownloadStatus.Text = $"The models were not moved: {failure.Message} They are still in {Options.ModelsFolder}.";
            ModelsChangeFolder.IsEnabled = true;
        }
    }

    // The models folder is read once at start, by every operation; a new start reads the new one.
    private static void Restart()
    {
        if (Environment.ProcessPath is { } exe)
        {
            ProcessStartInfo start = new(exe) { UseShellExecute = false };
            foreach (string arg in Environment.GetCommandLineArgs().Skip(1))
            {
                start.ArgumentList.Add(arg);
            }

            Process.Start(start);
        }

        Application.Current.Shutdown();
    }
}
