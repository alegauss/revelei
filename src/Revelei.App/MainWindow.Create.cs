using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Revelei.Generation;
using Revelei.Imaging;
using Revelei.Inference;

namespace Revelei.App;

/// <summary>A shape the Create screen offers, at the resolution the models were trained around (about 1 MP).</summary>
/// <param name="Label">What the picker says.</param>
/// <param name="Width">Columns.</param>
/// <param name="Height">Rows.</param>
public sealed record CreateShape(string Label, int Width, int Height)
{
    /// <summary>The shapes, square first.</summary>
    public static IReadOnlyList<CreateShape> All { get; } =
    [
        new("Square 1:1 · 1024 × 1024", 1024, 1024),
        new("Landscape 3:2 · 1216 × 832", 1216, 832),
        new("Portrait 2:3 · 832 × 1216", 832, 1216),
        new("Wide 16:9 · 1344 × 768", 1344, 768),
        new("Tall 9:16 · 768 × 1344", 768, 1344),
    ];

    /// <summary>The label, which is also each item's accessible name in the picker.</summary>
    /// <returns>The label.</returns>
    public override string ToString() => Label;
}

/// <summary>An installed generation model as the picker lists it.</summary>
/// <param name="Model">The model.</param>
public sealed record CreateModelChoice(GenerationModel Model)
{
    /// <summary>What the picker says.</summary>
    public string Label => $"{Model.Title} · {Model.Quantisation}";

    /// <summary>The label, which is also each item's accessible name in the picker.</summary>
    /// <returns>The label.</returns>
    public override string ToString() => Label;
}

/// <summary>
/// The Create screen (PB80): a prompt, an installed generation model (PB79) and its settings to
/// an image, generated through Revelei.Generation's host process (PB78). The image forms in the
/// centre from the engine's step previews; Generate turns into Cancel while it runs, and the
/// status line says the stage, the step, the time taken and, after a step, the time left. Leaving
/// Create, or ten idle minutes, ends the host and frees the model's memory.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan CreateIdle = TimeSpan.FromMinutes(10);

    private GenerationClient? generation;

    private CancellationTokenSource? generating;

    private DispatcherTimer? createIdle;

    private void OnCreateClick(object sender, RoutedEventArgs e)
    {
        bool opening = CreatePanel.Visibility != Visibility.Visible;
        if (opening)
        {
            if (AboutPanel.Visibility == Visibility.Visible)
            {
                OnAboutClick(this, new RoutedEventArgs());
            }

            if (ModelsPanel.Visibility == Visibility.Visible)
            {
                OnModelsClick(this, new RoutedEventArgs());
            }

            ConsentPanel.Visibility = Visibility.Collapsed;
            consenting = null;
            FillCreate();
        }
        else
        {
            generating?.Cancel();
            _ = EndGenerationAsync();
        }

        CreatePanel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
        Stage.Visibility = opening ? Visibility.Collapsed : Visibility.Visible;
        CreateButton.Content = opening ? "Back" : "Create";
    }

    /// <summary>Lists the installed generation models and sets the screen to the first one's settings.</summary>
    private void FillCreate()
    {
        machine ??= MachineProfile.Measure(Options.ModelsFolder);
        string? chosen = (CreateModel.SelectedItem as CreateModelChoice)?.Model.Name;
        List<CreateModelChoice> models = [.. GenerationModels.Installed(Options.ModelsFolder, Manifest, machine).Select(m => new CreateModelChoice(m))];
        CreateModel.ItemsSource = models;
        CreateModel.SelectedItem = models.FirstOrDefault(m => m.Model.Name == chosen) ?? models.FirstOrDefault();
        CreateNoModel.Visibility = models.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (CreateAspect.ItemsSource is null)
        {
            CreateAspect.ItemsSource = CreateShape.All;
            CreateAspect.SelectedIndex = 0;
            CreatePrompt.Text = AppSettings.Load(AppSettings.DefaultPath).Prompt ?? string.Empty;
            CreateSeed.Text = NewSeed();
        }

        bool engine = Options.EngineArguments is not null;
        CreateGenerate.IsEnabled = generating is not null || (models.Count > 0 && engine);
        if (!engine)
        {
            CreateStatus.Text = "This copy of Revelei has no generation engine; a new version of Revelei installs it.";
        }
    }

    private void OnCreateModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CreateModel.SelectedItem is CreateModelChoice choice)
        {
            ResetCreateSettings(choice.Model);
            CreateFit.Text = choice.Model.Verdict is { } verdict ? verdict.Reason : string.Empty;
        }
    }

    private void OnCreateResetClick(object sender, RoutedEventArgs e)
    {
        if (CreateModel.SelectedItem is CreateModelChoice choice)
        {
            ResetCreateSettings(choice.Model);
        }
    }

    private void ResetCreateSettings(GenerationModel model)
    {
        CreateSteps.Text = model.Defaults.Steps.ToString(CultureInfo.CurrentCulture);
        CreateGuidance.Text = model.Defaults.Guidance.ToString("0.0#", CultureInfo.CurrentCulture);
    }

    private void OnCreateDiceClick(object sender, RoutedEventArgs e) => CreateSeed.Text = NewSeed();

    private static string NewSeed() => Random.Shared.NextInt64(0, int.MaxValue).ToString(CultureInfo.InvariantCulture);

    private async void OnCreateGenerateClick(object sender, RoutedEventArgs e)
    {
        if (generating is not null)
        {
            CreateStatus.Text = "Stopping after this step…";
            await generating.CancelAsync().ConfigureAwait(true);
            return;
        }

        if (CreateModel.SelectedItem is not CreateModelChoice { Model: { } model } || CreateAspect.SelectedItem is not CreateShape shape)
        {
            return;
        }

        if (!int.TryParse(CreateSteps.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int steps) || steps is < 1 or > 200
            || !float.TryParse(CreateGuidance.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out float guidance) || guidance is < 0 or > 30
            || !long.TryParse(CreateSeed.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seed))
        {
            CreateStatus.Text = "Steps must be a whole number from 1 to 200, guidance a number from 0 to 30, and the seed a whole number.";
            return;
        }

        string prompt = CreatePrompt.Text.Trim();
        if (prompt.Length == 0)
        {
            CreateStatus.Text = "Write what the image should show first.";
            return;
        }

        (AppSettings.Load(AppSettings.DefaultPath) with { Prompt = prompt }).Save(AppSettings.DefaultPath);
        bool offload = model.Verdict?.Fit == Fit.RunsSlower;
        GenerationRequest request = new(model.Transformer, model.TextEncoder, model.Vae, prompt, shape.Width, shape.Height, steps, guidance, seed, offload);

        createIdle?.Stop();
        generating = new CancellationTokenSource();
        CreateGenerate.Content = "Cancel";
        CreateStatus.Text = offload
            ? $"{model.Verdict!.Reason} Loading the model…"
            : "Loading the model…";
        Stopwatch clock = Stopwatch.StartNew();
        DateTime? firstStep = null;
        Progress<GenerationProgress> progress = new(p =>
        {
            if (p.Stage == GenerationStage.Sampling && p.Step == 1)
            {
                firstStep = DateTime.UtcNow;
            }

            CreateStatus.Text = CreateText.Line(p, clock.Elapsed, firstStep is { } at ? DateTime.UtcNow - at : null);
        });
        Progress<RgbaImage> preview = new(frame => CreateImage.Source = Bitmap(frame));
        try
        {
            generation ??= new GenerationClient(Path.Combine(AppContext.BaseDirectory, "Revelei.Generation.Host.exe"), Options.EngineArguments!);
            RgbaImage image = await generation.GenerateAsync(request, progress, generating.Token, preview).ConfigureAwait(true);
            CreateImage.Source = Bitmap(image);
            CreateStatus.Text = string.Create(CultureInfo.CurrentCulture, $"Done in {clock.Elapsed.TotalSeconds:0} s · {image.Width} × {image.Height} · seed {seed}.");
            if (CreateSeedLock.IsChecked != true)
            {
                CreateSeed.Text = NewSeed();
            }
        }
        catch (OperationCanceledException)
        {
            CreateStatus.Text = "Cancelled.";
        }
        catch (GenerationFailedException failure)
        {
            CreateStatus.Text = $"The image could not be made: {failure.Message}";
        }
        finally
        {
            generating.Dispose();
            generating = null;
            CreateGenerate.Content = "Generate";
            StartCreateIdle();
        }
    }

    // Ten minutes with nothing generated end the host, so a loaded 12 GB model does not stay in memory.
    private void StartCreateIdle()
    {
        createIdle ??= new DispatcherTimer(CreateIdle, DispatcherPriority.Background, async (_, _) =>
        {
            createIdle!.Stop();
            await EndGenerationAsync().ConfigureAwait(true);
        }, Dispatcher);
        createIdle.Stop();
        createIdle.Start();
    }

    private async Task EndGenerationAsync()
    {
        if (generating is null && generation is { } client)
        {
            generation = null;
            createIdle?.Stop();
            await client.DisposeAsync().ConfigureAwait(true);
        }
    }

    private static BitmapSource Bitmap(RgbaImage image)
    {
        byte[] bgra = new byte[image.Width * image.Height * 4];
        ReadOnlySpan<ushort> samples = image.Samples;
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = (byte)(samples[i + 2] >> 8);
            bgra[i + 1] = (byte)(samples[i + 1] >> 8);
            bgra[i + 2] = (byte)(samples[i] >> 8);
            bgra[i + 3] = (byte)(samples[i + 3] >> 8);
        }

        BitmapSource bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, bgra, image.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
