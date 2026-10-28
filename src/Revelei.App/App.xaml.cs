using System.IO;
using System.Windows;

namespace Revelei.App;

/// <summary>The application: parses the command line (PB39) and opens the main window with it.</summary>
public partial class App : Application
{
    /// <summary>The exit code for a command line that was refused.</summary>
    public const int BadCommandLine = 2;

    /// <inheritdoc/>
    protected override void OnStartup(StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnStartup(e);
        MoveLegacyData();

        ParsedCommandLine parsed = CommandLine.Parse(e.Args);
        if (!parsed.Ok)
        {
            MessageBox.Show(parsed.Error + "\n\nSee docs/COMMAND-LINE.md.", "Revelei", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(BadCommandLine);
            return;
        }

        // PB74: the models folder the person chose, unless --models names one for this run.
        MainWindow window = new(parsed.Options! with { ChosenModels = AppSettings.Load(AppSettings.DefaultPath).ModelsFolder });
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// Up to 0.3.0 the app was PhotoBoost and kept its downloaded models, engine cache and logs
    /// under %LOCALAPPDATA%\PhotoBoost. Moves that folder to the Revelei one once, so an upgrade
    /// does not download every model again. A move that fails leaves both folders as they were.
    /// </summary>
    private static void MoveLegacyData()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string legacy = Path.Combine(root, "PhotoBoost");
        string current = Path.Combine(root, "Revelei");
        if (!Directory.Exists(legacy) || Directory.Exists(current))
        {
            return;
        }

        try
        {
            Directory.Move(legacy, current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The models download again on first use, as on a new install.
        }
    }
}
