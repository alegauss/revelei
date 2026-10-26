using Revelei.Inference.Pipelines;

namespace Revelei.Inference.Tests;

/// <summary>PB52: the engines the operations need are built ahead, on the queue, and then not again.</summary>
public sealed class EngineWarmupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pb-warmup-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DirectMLAndCpuHaveNothingToBuild()
    {
        using ModelLibrary library = new(Path.Combine(Repository.Root, ".cache", "models"), new ProviderSelection(ExecutionProvider.Cpu, null, null));

        Assert.Null(library.Engines);
        Assert.Empty(EngineWarmup.Needed(library, "fp16"));
    }

    [Fact]
    public async Task TensorRTRtxBuildsWhatIsMissingOnceAndThenNothing()
    {
        WindowsMLDevice? device = WindowsMLProviders.Devices().FirstOrDefault(d => d.EpName == "NvTensorRTRTXExecutionProvider");
        if (device is null)
        {
            Assert.Skip("needs the TensorRT-RTX provider Windows ML installs for an NVIDIA RTX GPU");
        }

        // The probe model, laid out as the x4 upscaler's FP16, so only it is on disk to build.
        string models = Path.Combine(root, "models", Upscale.ModelFor(4));
        Directory.CreateDirectory(models);
        await File.WriteAllBytesAsync(Path.Combine(models, "fp16.onnx"), ProbeModel.Build(), TestContext.Current.CancellationToken);
        File.Copy(Path.Combine(Repository.Root, "scripts", "meta.example.json"), Path.Combine(models, "meta.json"));
        ProviderSelection selection = new(ExecutionProvider.WindowsML, device.AdapterIn(GpuAdapters.Enumerate()), null, device.EpName);
        using ModelLibrary library = new([Path.Combine(root, "models")], selection, engines: new EngineCache(Path.Combine(root, "engines")));
        await using InferenceService service = new();

        IReadOnlyList<ModelKey> needed = EngineWarmup.Needed(library, "fp16");
        Assert.Equal([new ModelKey(Upscale.ModelFor(4), "fp16")], needed);

        foreach (InferenceJob<ModelKey> job in EngineWarmup.Enqueue(service, library, needed))
        {
            await job.Completion;
        }

        Assert.Empty(EngineWarmup.Needed(library, "fp16"));
        Assert.Empty(library.Sessions.Loaded);
    }

    [Fact]
    public async Task UnderAutoAnUpscalerMovesToTensorRtOnceItsEngineIsBuiltAndTheRestStayOnDirectML()
    {
        WindowsMLDevice? device = WindowsMLProviders.Devices().FirstOrDefault(d => d.EpName == ProviderDetector.AutoAccelerator);
        if (device is null)
        {
            Assert.Skip("needs the TensorRT-RTX provider Windows ML installs for an NVIDIA RTX GPU");
        }

        // The probe model laid out as the x4 upscaler and as RMBG, both FP16.
        foreach (string model in new[] { Upscale.ModelFor(4), RemoveBackground.Model })
        {
            string folder = Path.Combine(root, "models", model);
            Directory.CreateDirectory(folder);
            await File.WriteAllBytesAsync(Path.Combine(folder, "fp16.onnx"), ProbeModel.Build(), TestContext.Current.CancellationToken);
            File.Copy(Path.Combine(Repository.Root, "scripts", "meta.example.json"), Path.Combine(folder, "meta.json"));
        }

        ProviderSelection auto = new(ExecutionProvider.WindowsML, device.AdapterIn(GpuAdapters.Enumerate()), null, device.EpName)
        {
            AcceleratedModels = ProviderDetector.AutoAcceleratedModels,
            EnginesReadyOnly = true,
        };
        using ModelLibrary library = new([Path.Combine(root, "models")], auto, engines: new EngineCache(Path.Combine(root, "engines")));
        await using InferenceService service = new();

        Assert.Equal("DirectML", library.RunsOn(Upscale.ModelFor(4), "fp16").Name);
        Assert.Equal([new ModelKey(Upscale.ModelFor(4), "fp16")], EngineWarmup.Needed(library, "fp16"));

        foreach (InferenceJob<ModelKey> job in EngineWarmup.Enqueue(service, library, EngineWarmup.Needed(library, "fp16")))
        {
            await job.Completion;
        }

        Assert.Equal("TensorRT-RTX", library.RunsOn(Upscale.ModelFor(4), "fp16").Name);
        Assert.Equal("DirectML", library.RunsOn(RemoveBackground.Model, "fp16").Name);
    }
}
