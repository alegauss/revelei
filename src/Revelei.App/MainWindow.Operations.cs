using System.Globalization;
using System.Windows;
using Revelei.Imaging;
using Revelei.Inference;
using Revelei.Inference.Pipelines;

namespace Revelei.App;

/// <summary>
/// The operations panel (PB24): runs an upscale on the inference queue (PB15) with tile progress
/// (PB20), a cancel, and the run logged (PB22) and summed up in the status bar. Face restore
/// (PB31) shares the queue, the progress card and the log; see MainWindow.Faces.cs.
/// </summary>
public partial class MainWindow
{
    private InferenceService? service;
    private ModelLibrary? library;
    private InferenceJob<RgbaImage>? running;
    private InferenceJob<FaceRestoreResult>? restoring;
    private readonly OperationLog log = new(OperationLog.DefaultFolder);

    /// <summary>The version shown when it is an operation's result rather than the opened file.</summary>
    public RgbaImage? Result => history.Position > 0 ? Image : null;

    /// <summary>The variant the quality choice names (PB29): FP16, the set the installer carries (PB51), or FP32 for High.</summary>
    private string Variant => QualityHigh.IsChecked == true ? "fp32" : "fp16";

    private int Factor => Scale2.IsChecked == true ? 2 : 4;

    /// <summary>Whether an operation is queued or running, which keeps every Run button off.</summary>
    private bool Busy => running is not null || restoring is not null;

    /// <summary>Called once the provider is known: opens the models folder and the queue.</summary>
    private void StartOperations(ProviderSelection provider)
    {
        library = new ModelLibrary(Options.ModelFolders, provider);
        service = new InferenceService();
        Closed += async (_, _) =>
        {
            running?.Cancel();
            restoring?.Cancel();
            await service.DisposeAsync().ConfigureAwait(false);
            library.Dispose();
            history.Dispose();
        };
        Scale2.Checked += (_, _) => RefreshOperations();
        Scale4.Checked += (_, _) => RefreshOperations();
        StartFirstUse();
        RefreshOperations();
    }

    /// <summary>Enables each operation only with an image, its models on disk, and nothing running.</summary>
    private void RefreshOperations()
    {
        Operations.Visibility = Image is null ? Visibility.Collapsed : Visibility.Visible;
        RefreshExport();
        // PB63: the toolbar's commands read Busy too, and a run starts and ends with no input to
        // make WPF ask them again.
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        if (library is null)
        {
            UpscaleButton.IsEnabled = false;
            RestoreFacesButton.IsEnabled = false;
            RemoveBackgroundButton.IsEnabled = false;
            DenoiseButton.IsEnabled = false;
            return;
        }

        RefreshQuality();
        string? missing = TooLarge(Factor) ?? Missing((Upscale.ModelFor(Factor), Variant));
        OperationNote.Text = missing ?? string.Empty;
        OperationNote.Visibility = missing is null ? Visibility.Collapsed : Visibility.Visible;
        UpscaleButton.IsEnabled = Image is not null && missing is null && !Busy;
        RefreshDenoise();
        RefreshFaces();
        RefreshBackground();
        RunPending();
    }

    /// <summary>
    /// PB29: High quality is offered once the FP32 set is on disk, and the note says how to get it
    /// or what it buys. A --quality high start keeps it chosen, and each card then names what is missing.
    /// </summary>
    private void RefreshQuality()
    {
        bool installed = HighQualityInstalled();
        QualityHigh.IsEnabled = installed || QualityHigh.IsChecked == true;
        QualityNote.Text = !installed
            ? "High quality needs the FP32 models: download them in About."
            : Provider?.Provider == ExecutionProvider.Cpu
                ? "High quality uses the FP32 models, which on this PC's CPU are also about twice as fast."
                : "High quality uses the FP32 models; on the GPU both run at about the same speed.";
    }

    private void OnQualityChanged(object sender, RoutedEventArgs e)
    {
        if (QualityNote is not null)
        {
            RefreshOperations(); // Not while InitializeComponent is still building the card.
        }
    }

    /// <summary>The note saying an upscale by the factor would be too large to hold, or null when it fits.</summary>
    private string? TooLarge(int factor)
    {
        if (Image is not { } image || Upscale.Fits(image.Width, image.Height, factor))
        {
            return null;
        }

        int side = (int)Math.Sqrt(Upscale.MaxOutputPixels);
        string smaller = factor == 4 && Upscale.Fits(image.Width, image.Height, 2) ? "Choose 2×, or" : "Use";
        return $"{factor}× would make this photo {(long)image.Width * factor} × {(long)image.Height * factor}, past the {side} × {side} Revelei can hold. {smaller} a smaller photo.";
    }

    /// <summary>The note naming the first model not on disk, or null when all are.</summary>
    private string? Missing(params (string Model, string Variant)[] needed) =>
        needed.FirstOrDefault(one => !library!.Has(one.Model, one.Variant)) is { Model: not null } absent
            ? $"{absent.Model} ({absent.Variant}) is not in {Options.ModelsFolder}. Download the models first."
            : null;

    private async void OnUpscaleClick(object sender, RoutedEventArgs e)
    {
        if (Image is not { } source || service is null || library is null)
        {
            return;
        }

        int factor = Factor;
        Progress<JobProgress> progress = new(ShowProgress);
        running = UpscaleJob.Enqueue(service, library, source, factor, Variant, progress, log);
        BeginJob($"Upscale {factor}×", "Starting…");

        try
        {
            RgbaImage result = await running.Completion.ConfigureAwait(true);
            running = null;
            PushVersion(result, $"Upscale {factor}×");
            JobStatus.Text = $"Done: {result.Width} × {result.Height}.";
            JobProgress.Value = JobProgress.Maximum;
            await ExportAfterRunAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            JobStatus.Text = "Cancelled.";
        }
        catch (Exception failure) when (failure is InvalidOperationException or OutOfMemoryException or ArithmeticException or System.IO.IOException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException)
        {
            JobStatus.Text = failure.Message;
        }
        finally
        {
            running = null;
            EndJob();
            ShowLastRun();
            RefreshOperations();
        }
    }

    /// <summary>
    /// PB60: shows a run that just started in the pinned card above the operations and in the
    /// status bar, so its progress and its end are seen without scrolling.
    /// </summary>
    private void BeginJob(string title, string status)
    {
        JobTitle.Text = title;
        JobStatus.Text = status;
        JobProgress.Maximum = 1;
        JobProgress.Value = 0;
        JobPanel.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Visible;
        CancelButton.IsEnabled = true;
        JobChip.Visibility = Visibility.Visible;
        RefreshOperations();
    }

    /// <summary>The run ended: the card keeps its outcome, and Cancel and the status bar's chip go.</summary>
    private void EndJob()
    {
        CancelButton.Visibility = Visibility.Collapsed;
        JobChip.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// PB61: the last run's outcome goes once the version shown is no longer its result: an undo,
    /// a redo or another photo would otherwise leave "Done" and its time describing an image the
    /// person has moved away from. Only called with nothing running, since undo waits for a run.
    /// </summary>
    private void ClearRunStatus()
    {
        JobPanel.Visibility = Visibility.Collapsed;
        LastRunChip.Visibility = Visibility.Collapsed;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        running?.Cancel();
        restoring?.Cancel();
        CancelButton.IsEnabled = false;
        JobStatus.Text = restoring is null ? "Cancelling after this tile…" : "Cancelling after this step…";
    }

    private void ShowProgress(JobProgress p)
    {
        JobProgress.Maximum = Math.Max(1, p.Total);
        JobProgress.Value = p.Done;
        string left = p.Remaining is { } remaining && p.Done < p.Total
            ? $" · about {Math.Ceiling(remaining.TotalSeconds).ToString(CultureInfo.CurrentCulture)} s left"
            : string.Empty;
        JobStatus.Text = p.Stage switch
        {
            FaceRestore.Stage => $"Face {p.Done} of {p.Total}{left}",
            RemoveBackground.Stage => p.Done < p.Total ? "Finding the subject…" : "Subject found.",
            RemoveBackground.EdgesStage when p.Total == 1 => p.Done < p.Total ? "Refining the edges…" : "Edges refined.",
            RemoveBackground.EdgesStage => $"Refining the edges: tile {p.Done} of {p.Total}{left}",
            _ => $"Tile {p.Done} of {p.Total}{left}",
        };
    }

    private void ShowLastRun()
    {
        if (log.ReadCurrent() is { Count: > 0 } records)
        {
            OperationRecord last = records[^1];
            string seconds = (last.Milliseconds / 1000).ToString("0.0", CultureInfo.CurrentCulture);
            LastRun.Text = $"{last.Operation}: {seconds} s on {last.Provider}{(last.Outcome == "ok" ? string.Empty : $" ({last.Outcome})")}";
            LastRunChip.Visibility = Visibility.Visible;
        }
    }
}
