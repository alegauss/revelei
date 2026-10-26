using System.Windows;
using System.Windows.Input;
using Revelei.Imaging;
using Revelei.Render;

namespace Revelei.App;

/// <summary>
/// The history (PB28): every operation's result is a version; undo and redo move between them,
/// and operations and export always work on the one shown.
/// </summary>
public partial class MainWindow
{
    private readonly ImageHistory history = new();

    /// <summary>The label of the version shown, e.g. "Upscale 4×".</summary>
    public string? VersionLabel => history.Position < 0 ? null : history.Labels[history.Position];

    /// <summary>Records a finished operation as the new current version and shows it.</summary>
    private void PushVersion(RgbaImage image, string label)
    {
        history.Push(image, label);
        unsaved = true;
        ShowVersion(image);
    }

    /// <summary>Shows a version: picture, caption, history list, and what the panel can do with it.</summary>
    private void ShowVersion(RgbaImage image)
    {
        Image = image;
        ViewerOpenError.Visibility = Visibility.Collapsed;
        EndCrop(); // PB67: a rectangle drawn on the last version means nothing on this one.
        // PB27: the surface compares the version shown against the opened file.
        Surface.Palette = Brand.Surface;
        Surface.Show(history.Position > 0 ? history.Original : null, image);
        bool comparable = history.Position > 0;
        ModeDivider.IsEnabled = comparable;
        ModeSideBySide.IsEnabled = comparable;
        if (!comparable)
        {
            ModeSingle.IsChecked = true;
        }
        string name = System.IO.Path.GetFileName(ImagePath) ?? string.Empty;
        ImageInfo.Text = history.Position > 0
            ? $"{name} — {VersionLabel} — {image.Width} × {image.Height}"
            : $"{name} — {image.Width} × {image.Height}";
        // Numbered, so two "Upscale 2×" steps are two items: WPF keys a list's items, and their
        // automation peers, by value, and equal strings collapse into one.
        HistoryList.ItemsSource = history.Labels.Select((label, i) => $"{i + 1}. {label}").ToList();
        HistoryList.SelectedIndex = history.Position;
        CommandManager.InvalidateRequerySuggested();
        RefreshOperations();
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (Surface is null)
        {
            return; // A radio button checked while the window is still being built.
        }

        Surface.Mode = ModeDivider.IsChecked == true ? CompareMode.Divider
            : ModeSideBySide.IsChecked == true ? CompareMode.SideBySide
            : CompareMode.Single;
    }

    private void OnFitClick(object sender, RoutedEventArgs e) => Surface.ResetView();

    private void CanUndo(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = !Busy && history.CanUndo;

    private void CanRedo(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = !Busy && history.CanRedo;

    private void OnUndo(object sender, ExecutedRoutedEventArgs e)
    {
        if (history.Undo() is { } image)
        {
            ClearRunStatus();
            ShowVersion(image);
        }
    }

    private void OnRedo(object sender, ExecutedRoutedEventArgs e)
    {
        if (history.Redo() is { } image)
        {
            ClearRunStatus();
            ShowVersion(image);
        }
    }
}
