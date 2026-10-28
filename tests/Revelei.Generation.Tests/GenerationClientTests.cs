using Revelei.Imaging;

namespace Revelei.Generation.Tests;

/// <summary>
/// PB78: the client drives the host process over its line protocol: progress by stage, the
/// image back, a cancel that stops after the step, a cancel that does not land ending the host,
/// and a host that crashes failing the one generation and starting again for the next. The fake
/// engine stands in for stable-diffusion.cpp so this runs on any desk.
/// </summary>
public sealed class GenerationClientTests
{
    private static readonly string Host = Path.Combine(AppContext.BaseDirectory, "Revelei.Generation.Host.exe");

    private static GenerationRequest Request(string prompt = "a fox", int steps = 6) => new("d.gguf", "te.gguf", "vae.safetensors", prompt, 64, 48, steps, Seed: 7);

    [Fact]
    public async Task AGenerationReportsEachStageAndReturnsTheImage()
    {
        await using GenerationClient client = new(Host, ["--fake"]);
        List<GenerationProgress> seen = [];

        RgbaImage image = await client.GenerateAsync(Request(), new Inline(seen), TestContext.Current.CancellationToken);

        Assert.Equal((64, 48), (image.Width, image.Height));
        Assert.Equal(7 * 257, image.Samples[2]);
        Assert.Equal(RgbaImage.Max, image.Samples[3]);
        Assert.Equal([GenerationStage.Loading, .. Enumerable.Repeat(GenerationStage.Sampling, 6), GenerationStage.Decoding], seen.Select(p => p.Stage));
        Assert.Equal(new GenerationProgress(GenerationStage.Sampling, 6, 6), seen[^2]);
    }

    [Fact]
    public async Task EachSamplingStepSendsAPreviewOfTheImageAsItForms()
    {
        await using GenerationClient client = new(Host, ["--fake"]);
        List<RgbaImage> frames = [];

        await client.GenerateAsync(Request(steps: 3), null, TestContext.Current.CancellationToken, new Frames(frames));

        Assert.Equal([1 * 257, 2 * 257, 3 * 257], frames.Select(f => (int)f.Samples[0]));
        Assert.Empty(Directory.EnumerateFiles(Path.GetTempPath(), "revelei-generated-*"));
    }

    [Fact]
    public async Task TheHostStaysUpBetweenImages()
    {
        await using GenerationClient client = new(Host, ["--fake"]);

        await client.GenerateAsync(Request(), null, TestContext.Current.CancellationToken);
        Assert.True(client.HostRunning);
        RgbaImage second = await client.GenerateAsync(Request() with { Width = 32, Height = 32 }, null, TestContext.Current.CancellationToken);

        Assert.Equal(32, second.Width);
    }

    [Fact]
    public async Task ACancelStopsAfterTheStepAndTheHostServesTheNextOne()
    {
        await using GenerationClient client = new(Host, ["--fake"]);
        using CancellationTokenSource cancel = new();
        Cancelling progress = new(cancel, atStep: 2);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GenerateAsync(Request(steps: 50), progress, cancel.Token));

        Assert.InRange(progress.LastSamplingStep, 2, 4);
        Assert.True(client.HostRunning);
        Assert.Equal(64, (await client.GenerateAsync(Request(), null, TestContext.Current.CancellationToken)).Width);
    }

    [Fact]
    public async Task ACancelThatDoesNotLandEndsTheHost()
    {
        await using GenerationClient client = new(Host, ["--fake"]) { CancelGrace = TimeSpan.FromMilliseconds(500) };
        using CancellationTokenSource cancel = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GenerateAsync(Request("hang"), new Cancelling(cancel, atStep: 1), cancel.Token));

        Assert.False(client.HostRunning);
        Assert.Equal(64, (await client.GenerateAsync(Request(), null, TestContext.Current.CancellationToken)).Width);
    }

    [Fact]
    public async Task AHostThatCrashesFailsOneGenerationAndStartsAgain()
    {
        await using GenerationClient client = new(Host, ["--fake"]);

        GenerationFailedException failed = await Assert.ThrowsAsync<GenerationFailedException>(() => client.GenerateAsync(Request("crash"), null, TestContext.Current.CancellationToken));

        Assert.StartsWith("The generation engine stopped.", failed.Message, StringComparison.Ordinal);
        Assert.Equal(64, (await client.GenerateAsync(Request(), null, TestContext.Current.CancellationToken)).Width);
    }

    [Fact]
    public async Task AHostThatCannotLoadItsEngineSaysWhy()
    {
        await using GenerationClient client = new(Host, ["--engine", Path.Combine(Path.GetTempPath(), "pb-no-engine-" + Guid.NewGuid().ToString("N"))]);

        GenerationFailedException failed = await Assert.ThrowsAsync<GenerationFailedException>(() => client.GenerateAsync(Request(), null, TestContext.Current.CancellationToken));

        Assert.StartsWith("The generation engine stopped.", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRealEngineGeneratesWhenItAndAModelAreOnThisDesk()
    {
        // REVELEI_SD_ENGINE names a stable-diffusion.cpp build (spike/diffusion/README.md) and
        // REVELEI_SD_MODELS a folder with Z-Image Turbo's three files; neither is in the repository.
        string? engine = Environment.GetEnvironmentVariable("REVELEI_SD_ENGINE");
        string? models = Environment.GetEnvironmentVariable("REVELEI_SD_MODELS");
        string[] files = ["z_image_turbo-Q4_K.gguf", "Qwen3-4B-Instruct-2507-Q4_K_M.gguf", "ae.safetensors"];
        if (engine is null || models is null || !File.Exists(Path.Combine(engine, "stable-diffusion.dll")) || !files.All(f => File.Exists(Path.Combine(models, f))))
        {
            Assert.Skip("Set REVELEI_SD_ENGINE and REVELEI_SD_MODELS to a stable-diffusion.cpp build and Z-Image Turbo's files to run the engine.");
        }

        await using GenerationClient client = new(Host, ["--engine", engine]);
        List<GenerationProgress> seen = [];
        List<RgbaImage> frames = [];
        GenerationRequest request = new(
            Path.Combine(models, files[0]), Path.Combine(models, files[1]), Path.Combine(models, files[2]),
            "A red fox sitting in fresh snow", 512, 512, 4);

        RgbaImage image = await client.GenerateAsync(request, new Inline(seen), TestContext.Current.CancellationToken, new Frames(frames));

        Assert.Equal((512, 512), (image.Width, image.Height));
        Assert.Contains(new GenerationProgress(GenerationStage.Sampling, 4, 4), seen);
        Assert.NotEmpty(frames);
    }

    private sealed class Inline(List<GenerationProgress> seen) : IProgress<GenerationProgress>
    {
        public void Report(GenerationProgress value)
        {
            lock (seen)
            {
                seen.Add(value);
            }
        }
    }

    private sealed class Frames(List<RgbaImage> frames) : IProgress<RgbaImage>
    {
        public void Report(RgbaImage value)
        {
            lock (frames)
            {
                frames.Add(value);
            }
        }
    }

    private sealed class Cancelling(CancellationTokenSource cancel, int atStep) : IProgress<GenerationProgress>
    {
        public int LastSamplingStep { get; private set; }

        public void Report(GenerationProgress value)
        {
            if (value.Stage == GenerationStage.Sampling)
            {
                LastSamplingStep = value.Step;
                if (value.Step == atStep)
                {
                    cancel.Cancel();
                }
            }
        }
    }
}
