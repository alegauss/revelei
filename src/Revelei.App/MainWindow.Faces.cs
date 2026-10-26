using System.Windows;
using Revelei.Inference;
using Revelei.Inference.Pipelines;

namespace Revelei.App;

/// <summary>
/// The Restore faces card (PB31): GFPGAN on each face YuNet finds, blended by the strength slider,
/// after an upscale by the Upscale card's factor when asked. It runs on the same queue, progress
/// card and log as an upscale.
/// </summary>
public partial class MainWindow
{
    private int FaceUpscaleFactor => FaceUpscale.IsChecked == true ? Factor : 1;

    /// <summary>Enables Restore faces only with an image, every model it needs, and nothing running.</summary>
    private void RefreshFaces()
    {
        // PB54: GFPGAN may run in another variant than the panel's, so the note names that one.
        string restorer = FaceRestore.VariantFor(library!, Variant);
        (string, string)[] needed = FaceUpscaleFactor == 1
            ? [(FaceDetection.Model, "fp32"), (FaceRestore.Model, restorer)]
            : [(FaceDetection.Model, "fp32"), (FaceRestore.Model, restorer), (Upscale.ModelFor(Factor), Variant)];

        // PB58: GFPGAN itself is downloaded on first use, so only the others can be "missing".
        bool fetch = FirstUse.Needed(FirstUse.Faces, library!, Manifest, Variant).Count > 0;
        string? missing = TooLarge(FaceUpscaleFactor) ?? Missing([.. needed.Where(one => !fetch || one.Item1 != FaceRestore.Model)]);
        FaceNote.Text = missing ?? string.Empty;
        FaceNote.Visibility = missing is null ? Visibility.Collapsed : Visibility.Visible;
        fetch = RefreshFirstUse(faceFirstUse!, Variant, blocked: missing is not null) && missing is null;
        RestoreFacesButton.Content = fetch ? $"Download and {FirstUse.Faces.Verb}" : FirstUse.Faces.Title;
        RestoreFacesButton.IsEnabled = Image is not null && missing is null && (fetch ? faceFirstUse!.Downloading is null && faceFirstUse.Stage == FirstUseStage.Offered : !Busy);
    }

    private void OnFaceOptionChanged(object sender, RoutedEventArgs e) => RefreshOperations();

    private async void OnRestoreFacesClick(object sender, RoutedEventArgs e)
    {
        if (Image is not { } source || service is null || library is null)
        {
            return;
        }

        if (faceFirstUse is { Needed.Count: > 0 })
        {
            ShowConsent(faceFirstUse);
            return;
        }

        int upscale = FaceUpscaleFactor;
        double strength = FaceStrength.Value / 100;
        restoring = FaceRestoreJob.Enqueue(service, library, source, Variant, strength, upscale, new Progress<JobProgress>(ShowProgress), log);
        BeginJob(upscale == 1 ? FirstUse.Faces.Title : $"Upscale {upscale}× + Restore faces", upscale == 1 ? "Finding faces…" : "Upscaling first…");

        try
        {
            FaceRestoreResult result = await restoring.Completion.ConfigureAwait(true);
            restoring = null;
            if (result.Faces == 0)
            {
                JobStatus.Text = "No faces found.";
                return;
            }

            string label = upscale == 1 ? "Restore faces" : $"Upscale {upscale}× + Restore faces";
            PushVersion(result.Image, label);
            JobStatus.Text = result.Faces == 1 ? "Done: 1 face." : $"Done: {result.Faces} faces.";
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
            restoring = null;
            EndJob();
            ShowLastRun();
            RefreshOperations();
        }
    }
}
