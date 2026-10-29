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

/// <summary>One image this session made, with what made it (PB80).</summary>
/// <param name="Image">The image.</param>
/// <param name="Thumbnail">The strip's copy.</param>
/// <param name="Prompt">Its prompt.</param>
/// <param name="Model">The model's folder name.</param>
/// <param name="Shape">Its shape.</param>
/// <param name="Steps">Its steps.</param>
/// <param name="Guidance">Its guidance.</param>
/// <param name="Seed">Its seed, which with the rest makes it again.</param>
public sealed record CreateResult(BitmapSource Image, BitmapSource Thumbnail, string Prompt, string Model, CreateShape Shape, int Steps, float Guidance, long Seed)
{
    /// <summary>What the strip says of it.</summary>
    public string Label => string.Create(CultureInfo.CurrentCulture, $"Seed {Seed} · {Shape.Width} × {Shape.Height}");

    /// <summary>The label, which is also its accessible name in the strip.</summary>
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

    private readonly System.Collections.ObjectModel.ObservableCollection<CreateResult> createResults = [];

    private (string Model, string Quantisation)? recommended;

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
        OfferRecommended(models.Count == 0);

        if (CreateAspect.ItemsSource is null)
        {
            CreateAspect.ItemsSource = CreateShape.All;
            CreateAspect.SelectedIndex = 0;
            CreateCount.ItemsSource = (int[])[1, 2, 3, 4];
            CreateCount.SelectedIndex = 0;
            CreateResults.ItemsSource = createResults;
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

    // With no model installed, the generation model this PC runs best (GenerationModels.Recommend).
    private void OfferRecommended(bool none)
    {
        recommended = null;
        if (none && machine is not null)
        {
            if (GenerationModels.Recommend(Manifest, machine) is { } choice && choice.Model is { } best)
            {
                long bytes = best.Quantisations![choice.Quantisation].Files.Sum(f => f.File.Bytes);
                recommended = (best.Name, choice.Quantisation);
                CreateRecommended.Text = $"Recommended for this PC: {FirstUse.NameOf(best.Name)} ({choice.Quantisation}), {DownloadText.Size(bytes)} to download, {best.License}. {choice.Verdict.Reason}";
                CreateDownloadRecommended.Content = $"Download {FirstUse.NameOf(best.Name)}";
            }
        }

        Visibility offered = recommended is null ? Visibility.Collapsed : Visibility.Visible;
        CreateRecommended.Visibility = offered;
        CreateDownloadRecommended.Visibility = offered;
        CreateDownloadRecommended.IsEnabled = !Queue.Items.Any(q => q.Model == recommended?.Model);
    }

    private void OnCreateDownloadRecommendedClick(object sender, RoutedEventArgs e)
    {
        if (recommended is { } offer)
        {
            Queue.Enqueue(offer.Model, offer.Quantisation);
            CreateDownloadRecommended.IsEnabled = false;
            CreateStatus.Text = "Downloading. The Models panel shows how far it is, and Create offers it once it is in.";
            RunQueue();
        }
    }

    private void OnCreateResultChanged(object sender, SelectionChangedEventArgs e)
    {
        CreateUseSettings.IsEnabled = CreateResults.SelectedItem is CreateResult;
        if (CreateResults.SelectedItem is CreateResult result && generating is null)
        {
            CreateImage.Source = result.Image;
            CreateStatus.Text = string.Create(CultureInfo.CurrentCulture, $"{FirstUse.NameOf(result.Model)} · {result.Shape.Width} × {result.Shape.Height} · {result.Steps} steps · guidance {result.Guidance:0.0#} · seed {result.Seed} · {result.Prompt}");
        }
    }

    private void OnCreateUseSettingsClick(object sender, RoutedEventArgs e)
    {
        if (CreateResults.SelectedItem is not CreateResult result)
        {
            return;
        }

        if (CreateModel.ItemsSource is IEnumerable<CreateModelChoice> models && models.FirstOrDefault(m => m.Model.Name == result.Model) is { } model)
        {
            CreateModel.SelectedItem = model;
        }

        CreatePrompt.Text = result.Prompt;
        CreateAspect.SelectedItem = result.Shape;
        CreateSteps.Text = result.Steps.ToString(CultureInfo.CurrentCulture);
        CreateGuidance.Text = result.Guidance.ToString("0.0#", CultureInfo.CurrentCulture);
        CreateSeed.Text = result.Seed.ToString(CultureInfo.InvariantCulture);
        CreateSeedLock.IsChecked = true;
    }

    private void OnCreateModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CreateModel.SelectedItem is CreateModelChoice choice)
        {
            ResetCreateSettings(choice.Model);
            CreateFit.Text = choice.Model.Verdict is { } verdict ? verdict.Reason : string.Empty;

            // A distilled model (guidance 1) ignores a negative prompt, so it is only asked of the others.
            CreateNegativeRow.Visibility = choice.Model.Defaults.Guidance > 1 ? Visibility.Visible : Visibility.Collapsed;
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
        int count = CreateCount.SelectedItem is int chosenCount ? chosenCount : 1;

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
            RgbaImage? image = null;

            // Each image takes the next seed, so every one of them can be made again from its own.
            for (int i = 0; i < count; i++)
            {
                firstStep = null;
                GenerationRequest request = new(
                    model.Transformer, model.TextEncoder, model.Vae, prompt, shape.Width, shape.Height, steps, guidance, seed + i, offload,
                    CreateNegativeRow.Visibility == Visibility.Visible && CreateNegative.Text.Trim() is { Length: > 0 } avoid ? avoid : null);
                image = await generation.GenerateAsync(request, progress, generating.Token, preview).ConfigureAwait(true);
                BitmapSource shown = Bitmap(image);
                CreateImage.Source = shown;
                createResults.Add(new CreateResult(shown, Thumbnail(shown), prompt, model.Name, shape, steps, guidance, seed + i));
            }

            string seeds = count == 1 ? $"seed {seed}" : $"seeds {seed} to {seed + count - 1}";
            CreateStatus.Text = string.Create(CultureInfo.CurrentCulture, $"Done in {clock.Elapsed.TotalSeconds:0} s · {image!.Width} × {image.Height} · {seeds}.");
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

    private static TransformedBitmap Thumbnail(BitmapSource image)
    {
        double scale = 160.0 / Math.Max(image.PixelWidth, image.PixelHeight);
        TransformedBitmap small = new(image, new ScaleTransform(scale, scale));
        small.Freeze();
        return small;
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
