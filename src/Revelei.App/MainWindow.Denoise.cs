using System.Windows;
using Revelei.Imaging;
using Revelei.Inference;
using Revelei.Inference.Pipelines;

namespace Revelei.App;

/// <summary>
/// The Denoise card (PB36): SCUNet over tiles, on the same queue, progress card and log as the
/// other operations.
/// </summary>
public partial class MainWindow
{
    /// <summary>Enables Denoise only with an image, its model, and nothing running.</summary>
    private void RefreshDenoise()
    {
        string? missing = Missing((Denoise.Model, Variant));
        DenoiseNote.Text = missing ?? string.Empty;
        DenoiseNote.Visibility = missing is null ? Visibility.Collapsed : Visibility.Visible;
        DenoiseButton.IsEnabled = Image is not null && missing is null && !Busy;
    }

    private async void OnDenoiseClick(object sender, RoutedEventArgs e)
    {
        if (Image is not { } source || service is null || library is null)
        {
            return;
        }

        running = DenoiseJob.Enqueue(service, library, source, Variant, new Progress<JobProgress>(ShowProgress), log);
        BeginJob("Denoise", "Starting…");

        try
        {
            RgbaImage result = await running.Completion.ConfigureAwait(true);
            running = null;
            PushVersion(result, "Denoise");
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
