using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Revelei.Imaging;
using Revelei.Render;

namespace Revelei.App;

/// <summary>
/// The toolbar's basic edits, the quarter turns (PB65), the flips (PB66) and the crop (PB67): each
/// is an exact move of the pixels, done on the UI thread since it takes well under a second, and
/// lands as a version in the history (PB28) like an operation's result. They wait for a running
/// operation as undo does.
/// </summary>
public partial class MainWindow
{
    /// <summary>A quarter turn clockwise, Ctrl+R.</summary>
    public static readonly RoutedUICommand RotateRightCommand = new(
        "Rotate right", "RotateRight", typeof(MainWindow), [new KeyGesture(Key.R, ModifierKeys.Control)]);

    /// <summary>A quarter turn counter-clockwise, Ctrl+Shift+R.</summary>
    public static readonly RoutedUICommand RotateLeftCommand = new(
        "Rotate left", "RotateLeft", typeof(MainWindow), [new KeyGesture(Key.R, ModifierKeys.Control | ModifierKeys.Shift)]);

    /// <summary>A mirror left to right; no shortcut, as in most editors.</summary>
    public static readonly RoutedUICommand FlipHorizontalCommand = new("Flip horizontal", "FlipHorizontal", typeof(MainWindow));

    /// <summary>A mirror top to bottom.</summary>
    public static readonly RoutedUICommand FlipVerticalCommand = new("Flip vertical", "FlipVertical", typeof(MainWindow));

    /// <summary>
    /// Starts or cancels the crop editor; C, which OnWindowKeyDown sends, since WPF refuses a
    /// KeyGesture of a letter with no modifier.
    /// </summary>
    public static readonly RoutedUICommand CropCommand = new("Crop", "Crop", typeof(MainWindow));

    /// <summary>The aspects the crop bar offers, in its order: free, the photo's own (NaN), then each and its portrait.</summary>
    private static readonly double?[] Aspects = [null, double.NaN, 1, 4.0 / 3, 3.0 / 4, 3.0 / 2, 2.0 / 3, 16.0 / 9, 9.0 / 16];

    /// <summary>Whether the crop editor is open.</summary>
    private bool Cropping => Surface.Crop is not null;

    private void CanEdit(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = Image is not null && !Busy && !Cropping;

    private void CanCrop(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = Image is not null && !Busy;

    private void OnRotateRight(object sender, ExecutedRoutedEventArgs e) => Edit(ImageTransforms.RotateRight, "Rotate right");

    private void OnRotateLeft(object sender, ExecutedRoutedEventArgs e) => Edit(ImageTransforms.RotateLeft, "Rotate left");

    private void OnFlipHorizontal(object sender, ExecutedRoutedEventArgs e) => Edit(ImageTransforms.FlipHorizontal, "Flip horizontal");

    private void OnFlipVertical(object sender, ExecutedRoutedEventArgs e) => Edit(ImageTransforms.FlipVertical, "Flip vertical");

    private void Edit(Func<RgbaImage, RgbaImage> edit, string label)
    {
        if (Image is not { } image || Busy)
        {
            return;
        }

        // PB61: the last run's outcome describes a version that is no longer the newest.
        ClearRunStatus();
        PushVersion(edit(image), label);
    }

    private void OnCrop(object sender, ExecutedRoutedEventArgs e)
    {
        if (Cropping)
        {
            EndCrop();
            return;
        }

        if (Image is not { } image)
        {
            return;
        }

        // The rectangle is drawn over the version shown alone, so a comparison gives way to it.
        ModeSingle.IsChecked = true;
        ModeDivider.IsEnabled = false;
        ModeSideBySide.IsEnabled = false;
        CropBar.Visibility = Visibility.Visible;
        Surface.CropChanged += OnCropChanged;
        Surface.CropAspect = AspectAt(CropAspect.SelectedIndex, image);
        Surface.Crop = CropMath.Fit(Surface.CropAspect, image.Width, image.Height);
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Closes the crop editor without changing the photo; any new version shown closes it too.</summary>
    private void EndCrop()
    {
        if (!Cropping)
        {
            return;
        }

        Surface.CropChanged -= OnCropChanged;
        Surface.Crop = null;
        CropBar.Visibility = Visibility.Collapsed;
        bool comparable = history.Position > 0;
        ModeDivider.IsEnabled = comparable;
        ModeSideBySide.IsEnabled = comparable;
        CommandManager.InvalidateRequerySuggested();
    }

    private void OnCropChanged(object? sender, EventArgs e) =>
        CropSize.Text = Surface.Crop is { } r ? $"{r.Width} × {r.Height} px" : string.Empty;

    private void OnCropAspectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Surface is null || !Cropping || Image is not { } image)
        {
            return; // The list is built before the surface, and only means something while cropping.
        }

        Surface.CropAspect = AspectAt(CropAspect.SelectedIndex, image);
        Surface.Crop = CropMath.Fit(Surface.CropAspect, image.Width, image.Height);
    }

    private static double? AspectAt(int index, RgbaImage image)
    {
        double? aspect = Aspects[Math.Max(0, index)];
        return aspect is { } a && double.IsNaN(a) ? (double)image.Width / image.Height : aspect;
    }

    private void OnCropApply(object sender, RoutedEventArgs e)
    {
        if (Surface.Crop is not { } rect || Image is not { } image)
        {
            return;
        }

        if (rect == image.Bounds)
        {
            EndCrop(); // Nothing to cut: no version for it.
            return;
        }

        Edit(photo => ImageTransforms.Crop(photo, rect), $"Crop {rect.Width} × {rect.Height}");
    }

    private void OnCropCancel(object sender, RoutedEventArgs e) => EndCrop();

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.None && e.OriginalSource is not TextBox
            && CropCommand.CanExecute(null, this))
        {
            CropCommand.Execute(null, this);
            e.Handled = true;
        }
    }
}
