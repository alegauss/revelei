using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Revelei.Imaging;
using Revelei.Inference;
using Revelei.Inference.Pipelines;

namespace Revelei.App;

/// <summary>The main window: the open screen (PB23), and the provider in the status bar (PB14).</summary>
public partial class MainWindow : Window
{
    private const string OpenFilter =
        "Images|*.png;*.jpg;*.jpeg;*.webp;*.tif;*.tiff;*.bmp;*.heic;*.heif;*.avif;*.jxr|All files|*.*";
    /// <summary>Builds the window from MainWindow.xaml and starts provider detection once it shows.</summary>
    /// <param name="options">What the command line asked for (PB39).</param>
    public MainWindow(AppOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        InitializeComponent();
        ThemeMode = Theme.Mode(Appearance.Default.Theme);
        QualityHigh.IsChecked = options.Quality == QualityMode.High;
        QualityStandard.IsChecked = options.Quality != QualityMode.High;
        Loaded += OnLoaded;
        Dispatcher.UnhandledException += OnUnhandled;
    }

    /// <summary>
    /// PB60: a failure no handler expected is said in the run's card instead of closing the app:
    /// a 4x of an 8192 px photo threw an overflow past every catch and took the window with it.
    /// The photo and its history are left as they were.
    /// </summary>
    private void OnUnhandled(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        JobTitle.Text = "Something went wrong";
        JobStatus.Text = $"{e.Exception.Message} The photo and its history are unchanged.";
        JobPanel.Visibility = Visibility.Visible;
        EndJob();
        e.Handled = true;
    }

    /// <summary>What the command line asked for; the screens that open, export and pick quality read it.</summary>
    public AppOptions Options { get; }

    /// <summary>The provider every model session uses, once detection has finished (PB14).</summary>
    public ProviderSelection? Provider { get; private set; }

    /// <summary>The version shown now (PB28), which operations and export work on; null on the drop zone.</summary>
    public RgbaImage? Image { get; private set; }

    /// <summary>The file it came from.</summary>
    public string? ImagePath { get; private set; }

    /// <summary>
    /// Decodes a file off the UI thread and shows it, or says why not. Every way in arrives here:
    /// the drop zone's button, the toolbar's Open and Ctrl+O, a drop anywhere on the window, and
    /// --open (PB39), which is how cases come.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>When the file is shown or refused.</returns>
    public async Task OpenAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string full = Path.GetFullPath(path);
        string name = Path.GetFileName(full);
        // PB64: opening replaces the photo and its history, so it waits for a run as undo does,
        // and asks before versions nobody saved are thrown away.
        if (Busy || (Image is not null && unsaved && !ConfirmDiscard(name)))
        {
            return;
        }

        RgbaImage image;
        try
        {
            if (!File.Exists(full))
            {
                throw new FileNotFoundException($"{name} was not found.", full);
            }

            image = await Task.Run(() => ImageDecoder.Decode(full)).ConfigureAwait(true);
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ShowError(failure is InvalidDataException ? $"{name} is not an image Windows can open." : failure.Message);
            return;
        }

        ImagePath = full;

        OpenError.Visibility = Visibility.Collapsed;
        DropZone.Visibility = Visibility.Collapsed;
        Viewer.Visibility = Visibility.Visible;
        history.Reset(image, $"Opened {name}");
        unsaved = false;
        ClearRunStatus();
        ShowVersion(image);
    }

    /// <summary>Whether the history holds a version made since the photo was opened or last saved.</summary>
    private bool unsaved;

    private bool ConfirmDiscard(string next)
    {
        string current = Path.GetFileName(ImagePath) ?? "This photo";
        return MessageBox.Show(
            this,
            $"{current} has versions that were not saved. Open {next} and discard them?",
            "Revelei",
            MessageBoxButton.YesNo,
            MessageBoxImage.None,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    /// <summary>
    /// Says why a file could not be opened: on the drop zone before the first photo, and over the
    /// photo once one is open, which stays on screen (PB64).
    /// </summary>
    private void ShowError(string message)
    {
        if (Image is not null)
        {
            ViewerOpenError.Text = message;
            ViewerOpenError.Visibility = Visibility.Visible;
            return;
        }

        OpenError.Text = message;
        OpenError.Visibility = Visibility.Visible;
        DropZone.Visibility = Visibility.Visible;
        Viewer.Visibility = Visibility.Collapsed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Options.ChosenModelsMissing)
        {
            DownloadChipText.Text = $"The models folder {Options.ChosenModels} cannot be found. Reconnect its drive, or download the models again.";
            DownloadChip.Visibility = Visibility.Visible;
        }

        if (Options.Open is { } open)
        {
            await OpenAsync(open).ConfigureAwait(true);
        }

        // A probe session takes a few hundred milliseconds on a GPU; the window does not wait.
        ProviderSelection selection = await Task.Run(() => ProviderDetector.Detect(Options.Provider)).ConfigureAwait(true);
        Provider = selection;
        ProviderStatus.Text = selection.Label;
        StartOperations(selection);
        if (selection.Provider == ExecutionProvider.WindowsML)
        {
            await PrepareEnginesAsync(selection).ConfigureAwait(true);
        }

        if (Options.Provider == ProviderPreference.WindowsML)
        {
            await AcquireWindowsMLAsync(selection).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Builds the Windows ML engines the operations will need, on the queue, one model at a time
    /// (PB52), so a user's first run loads a cached engine instead of building one for 10 to 17 s.
    /// </summary>
    private async Task PrepareEnginesAsync(ProviderSelection selection)
    {
        if (library is null || service is null)
        {
            return;
        }

        ModelLibrary models = library;
        string variant = Variant;
        IReadOnlyList<ModelKey> needed = await Task.Run(() => EngineWarmup.Needed(models, variant)).ConfigureAwait(true);
        int done = 0;
        foreach (InferenceJob<ModelKey> job in EngineWarmup.Enqueue(service, models, needed))
        {
            ProviderStatus.Text = $"{selection.Label} · preparing engines {done + 1} of {needed.Count}…";
            try
            {
                await job.Completion.ConfigureAwait(true);
            }
            catch (Exception failure) when (failure is OperationCanceledException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException or InvalidOperationException or IOException)
            {
                // That model builds on its first run instead, as it did before PB52.
            }

            done++;
        }

        ProviderStatus.Text = selection.Label;
    }

    /// <summary>
    /// Fetches the Windows ML providers this PC could use and does not have yet (PB43), once the
    /// window already works on what it has: the first fetch took 57 s through Windows Update in the
    /// PB5 spike, and offline it simply fails. They are used from the next start.
    /// </summary>
    private async Task AcquireWindowsMLAsync(ProviderSelection selection)
    {
        IReadOnlyList<string> missing = await Task.Run(WindowsMLProviders.Missing).ConfigureAwait(true);
        if (missing.Count == 0)
        {
            return;
        }

        string names = string.Join(", ", missing.Select(WindowsMLDevice.ShortName));
        ProviderStatus.Text = $"{selection.Label} · fetching {names}…";
        string? failure = await Task.Run(WindowsMLProviders.AcquireAsync).ConfigureAwait(true);
        ProviderStatus.Text = failure is null
            ? $"{selection.Label} · {names} ready from the next start"
            : $"{selection.Label} · {names} not fetched: {failure}";
    }

    // PB63: the toolbar's Open and Ctrl+O; while a run is busy they wait, as undo does.
    private void CanOpen(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = !Busy;

    private void OnOpenCommand(object sender, ExecutedRoutedEventArgs e) => OnOpenClick(sender, e);

    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new() { Filter = OpenFilter, Title = "Open a photo" };
        if (dialog.ShowDialog(this) == true)
        {
            await OpenAsync(dialog.FileName).ConfigureAwait(true);
        }
    }

    // PB64: a drop is taken anywhere on the window, the photo surface included: its child window
    // registers no drop target, so OLE hands the drop to this window's, and WPF routes it here.
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = !Busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            // Opened once the drop has returned: the discard prompt is modal, and inside the drop
            // it would hold Explorer's drag until it was answered.
            string path = files[0];
            Dispatcher.BeginInvoke(async () => await OpenAsync(path).ConfigureAwait(true));
        }
    }
}
