using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Revelei.Imaging;

namespace Revelei.App;

/// <summary>
/// The export card (PB25): a format, a quality for the lossy ones, a background when a transparent
/// image goes to JPEG, and Save as…; --export writes after every finished run without a dialog.
/// What is written is the result, or the opened image itself before any run.
/// </summary>
public partial class MainWindow
{
    private static readonly ImageFormat[] Formats = [ImageFormat.Png, ImageFormat.Jpeg, ImageFormat.Webp];

    private ImageFormat SelectedFormat => Formats[Math.Max(0, ExportFormat.SelectedIndex)];

    private RgbaImage? Exportable => Image;

    /// <summary>Shows the quality slider and the transparency warning only where they apply.</summary>
    private void RefreshExport()
    {
        if (QualityRow is null)
        {
            return; // Called while InitializeComponent is still building the card.
        }

        QualityRow.Visibility = SelectedFormat == ImageFormat.Png ? Visibility.Collapsed : Visibility.Visible;
        bool loses = Exportable is { } image && LosesTransparency(image, SelectedFormat);
        TransparencyRow.Visibility = loses ? Visibility.Visible : Visibility.Collapsed;
        MetadataNote.Visibility = KeepMetadata.IsChecked == true && !ImageEncoder.CarriesMetadata(SelectedFormat)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnExportOptionChanged(object sender, RoutedEventArgs e) => RefreshExport();

    private (RgbaImage Image, ImageFormat Format, bool Loses)? transparency;

    // Every refresh asks, and a refresh comes four times a second while a model downloads (PB58),
    // so an 8192 px photo's 67 million alphas are read once per version and format, not each time.
    private bool LosesTransparency(RgbaImage image, ImageFormat format)
    {
        if (transparency is not { } known || !ReferenceEquals(known.Image, image) || known.Format != format)
        {
            transparency = (image, format, ImageEncoder.LosesTransparency(image, format));
        }

        return transparency.Value.Loses;
    }

    private EncodeOptions OptionsFor(ImageFormat format) => new(
        format,
        Quality: (int)ExportQuality.Value,
        Background: ExportBackground.SelectedIndex == 1 ? Brand.JpegNight : Brand.JpegPaper,
        MetadataFrom: KeepMetadata.IsChecked == true ? ImagePath : null);

    // PB63: the SaveAs command, which the card's Save as…, the toolbar's and Ctrl+S share.
    private void CanSaveAs(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = Exportable is not null;

    private async void OnSaveAs(object sender, ExecutedRoutedEventArgs e)
    {
        if (Exportable is null)
        {
            return;
        }

        ImageFormat format = SelectedFormat;
        string extension = format switch { ImageFormat.Jpeg => ".jpg", ImageFormat.Webp => ".webp", _ => ".png" };
        SaveFileDialog dialog = new()
        {
            Title = "Export",
            Filter = $"{format} image|*{extension}",
            DefaultExt = extension,
            FileName = Path.GetFileNameWithoutExtension(ImagePath) + (Result is null ? string.Empty : "-revelei") + extension,
        };
        if (dialog.ShowDialog(this) == true)
        {
            await SaveAsync(dialog.FileName, OptionsFor(format)).ConfigureAwait(true);
        }
    }

    /// <summary>--export: writes after a run, in the format its extension names, with the card's quality.</summary>
    private async Task ExportAfterRunAsync()
    {
        if (Options.Export is not { } path)
        {
            return;
        }

        if (ImageEncoder.FormatFor(path) is not { } format)
        {
            ShowExportStatus($"--export {Path.GetFileName(path)}: use .png, .jpg or .webp.");
            return;
        }

        await SaveAsync(path, OptionsFor(format)).ConfigureAwait(true);
    }

    private async Task SaveAsync(string path, EncodeOptions options)
    {
        RgbaImage image = Exportable!;
        string full = Path.GetFullPath(path);
        try
        {
            await Task.Run(() => ImageEncoder.Save(image, full, options)).ConfigureAwait(true);
            unsaved = false;
            ShowExportStatus($"Saved {Path.GetFileName(full)}");
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowExportStatus($"Could not save {Path.GetFileName(full)}: {failure.Message}");
        }
    }

    private void ShowExportStatus(string text)
    {
        ExportStatus.Text = text;
        ExportStatus.Visibility = Visibility.Visible;
    }
}
