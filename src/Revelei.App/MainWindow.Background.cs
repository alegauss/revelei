using System.Windows;
using Revelei.Imaging;
using Revelei.Inference;
using Revelei.Inference.Pipelines;

namespace Revelei.App;

/// <summary>
/// The Remove background card (PB32): RMBG-1.4's mask becomes the image's alpha, on the same queue,
/// progress card and log as the other operations. PNG, the export default, keeps it.
/// </summary>
public partial class MainWindow
{
    /// <summary>Enables Remove background only with an image, its model, and nothing running.</summary>
    private void RefreshBackground()
    {
        // PB58: RMBG-1.4 is downloaded on first use; with nothing else needed, nothing is "missing".
        bool fetch = RefreshFirstUse(backgroundFirstUse!, Variant, blocked: false);
        string? missing = fetch ? null : Missing((RemoveBackground.Model, RemoveBackground.VariantFor(library!, Variant)));
        BackgroundNote.Text = missing ?? string.Empty;
        BackgroundNote.Visibility = missing is null ? Visibility.Collapsed : Visibility.Visible;
        RemoveBackgroundButton.Content = fetch ? $"Download and {FirstUse.Background.Verb}" : FirstUse.Background.Title;
        RemoveBackgroundButton.IsEnabled = Image is not null && missing is null
            && (fetch ? backgroundFirstUse!.Downloading is null && backgroundFirstUse.Stage == FirstUseStage.Offered : !Busy);
    }

    private async void OnRemoveBackgroundClick(object sender, RoutedEventArgs e)
    {
        if (Image is not { } source || service is null || library is null)
        {
            return;
        }

        if (backgroundFirstUse is { Needed.Count: > 0 })
        {
            ShowConsent(backgroundFirstUse);
            return;
        }

        bool refine = RefineEdges.IsChecked == true;
        running = RemoveBackgroundJob.Enqueue(service, library, source, Variant, refine, new Progress<JobProgress>(ShowProgress), log);
        BeginJob(FirstUse.Background.Title, "Finding the subject…");

        try
        {
            RgbaImage result = await running.Completion.ConfigureAwait(true);
            running = null;
            PushVersion(result, refine ? "Remove background" : "Remove background (unrefined)");
            JobStatus.Text = "Done.";
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
}
