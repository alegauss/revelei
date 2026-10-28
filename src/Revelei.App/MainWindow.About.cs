using System.IO;
using System.Reflection;
using System.Windows;
using Revelei.Inference;

namespace Revelei.App;

/// <summary>
/// The About panel (PB46): the version, every model the models folder holds with the license its
/// meta.json carries (so it never drifts from what export-and-quantize.py adopted), and the
/// third-party notices the installer collected (PB35).
/// </summary>
public partial class MainWindow
{
    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        bool opening = AboutPanel.Visibility != Visibility.Visible;
        if (opening)
        {
            if (ModelsPanel.Visibility == Visibility.Visible)
            {
                OnModelsClick(this, new RoutedEventArgs());
            }

            if (CreatePanel.Visibility == Visibility.Visible)
            {
                OnCreateClick(this, new RoutedEventArgs()); // PB80: one panel in the stage's place at a time.
            }

            FillAbout();
            ConsentPanel.Visibility = Visibility.Collapsed; // About replaces an unanswered offer; the card still has it.
            consenting = null;
        }

        AboutPanel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
        Stage.Visibility = opening ? Visibility.Collapsed : Visibility.Visible;
        AboutButton.Content = opening ? "Back" : "About";
    }

    private void FillAbout()
    {
        string version = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        AboutVersion.Text = $"Version {version}";
        // PB51: the bundled folder only when the installer put one there.
        AboutModelsFolder.Text = $"From {string.Join(" and ", Options.ModelFolders.Where(folder => folder == Options.ModelsFolder || Directory.Exists(folder)))}";

        // Before the provider is known there is no library yet; reading meta.json needs no session.
        IReadOnlyList<InstalledModel> installed;
        if (library is not null)
        {
            installed = library.Installed();
        }
        else
        {
            using ModelLibrary reading = new(Options.ModelFolders, new ProviderSelection(ExecutionProvider.Cpu, null, null));
            installed = reading.Installed();
        }

        AboutModels.ItemsSource = installed.Count == 0
            ? ["No models yet. Download them to use the operations."]
            : installed.Select(Describe).ToList();
        // PB75: a line on what is installed; the Models panel manages them.
        int downloaded = ModelUpdates.States(Manifest, Options.ModelsFolder).Count;
        long disk = Directory.Exists(Options.ModelsFolder)
            ? new DirectoryInfo(Options.ModelsFolder).GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
            : 0;
        AboutInstalled.Text = $"{installed.Count} models installed, {downloaded} of them downloaded ({DownloadText.Size(disk)}).";
        AboutNotices.Text = Notices();
        RefreshDownloads();
    }

    private static string Describe(InstalledModel model) =>
        $"{model.Name} · {model.Meta.Task} · {string.Join(", ", model.Variants)} · {model.Meta.Upstream.License} ({model.Meta.Upstream.LicenseUrl})";

    /// <summary>The notices the installer put beside the exe, or what ships without them.</summary>
    private static string Notices()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "licenses");
        string readme = Path.Combine(folder, "README.txt");
        if (File.Exists(readme))
        {
            IEnumerable<string> packages = File.ReadLines(readme).Where(line => line.Contains(": ", StringComparison.Ordinal));
            return string.Join(Environment.NewLine, packages) + $"{Environment.NewLine}{Environment.NewLine}Each package's own license and notices are in {folder}.";
        }

        return "Windows ML, its ONNX Runtime (MIT) and DirectML (Microsoft software license terms), SkiaSharp (MIT), Vortice and SharpGen (MIT). "
            + "An installed copy carries each package's own license in its licenses folder.";
    }
}
