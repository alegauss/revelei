using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Revelei.Inference.Tests;

/// <summary>
/// PB52: a Windows ML provider's engine is compiled once into an EP context model, keyed by what
/// makes it stale, and loaded from there afterwards.
/// </summary>
public sealed class EngineCacheTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "pb-engines-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void EveryPartOfTheKeyNamesADifferentEngine()
    {
        string model = Model();
        EngineCache cache = new(folder);
        string[] keys =
        [
            EngineCache.Key(model, "NvTensorRTRTXExecutionProvider", "TRT-RTX.EP_2.30.49.0", "0:4318:RTX 4060", "1", "1.24.6"),
            EngineCache.Key(model, "OpenVINOExecutionProvider", "TRT-RTX.EP_2.30.49.0", "0:4318:RTX 4060", "1", "1.24.6"),
            EngineCache.Key(model, "NvTensorRTRTXExecutionProvider", "TRT-RTX.EP_2.31.0.0", "0:4318:RTX 4060", "1", "1.24.6"),
            EngineCache.Key(model, "NvTensorRTRTXExecutionProvider", "TRT-RTX.EP_2.30.49.0", "1:4318:RTX 4070", "1", "1.24.6"),
            EngineCache.Key(model, "NvTensorRTRTXExecutionProvider", "TRT-RTX.EP_2.30.49.0", "0:4318:RTX 4060", "2", "1.24.6"),
            EngineCache.Key(model, "NvTensorRTRTXExecutionProvider", "TRT-RTX.EP_2.30.49.0", "0:4318:RTX 4060", "1", "1.25.0"),
        ];

        string[] paths = [.. keys.Select(key => cache.PathFor(model, key))];

        Assert.Equal(paths.Length, paths.Distinct(StringComparer.Ordinal).Count());
        Assert.All(paths, path => Assert.Matches(@"probe-fp16-[0-9a-f]{16}_ctx\.onnx$", path));
    }

    [Fact]
    public void AChangedModelFileNamesADifferentEngine()
    {
        string model = Model();
        string before = EngineCache.Key(model, "p", "pkg", "d", "1", "r");
        File.SetLastWriteTimeUtc(model, DateTime.UtcNow.AddMinutes(1));

        Assert.NotEqual(before, EngineCache.Key(model, "p", "pkg", "d", "1", "r"));
    }

    [Fact]
    public void TensorRTRtxCompilesOnceAndLoadsTheEngineAfterwards()
    {
        WindowsMLDevice? device = WindowsMLProviders.Devices().FirstOrDefault(d => d.EpName == "NvTensorRTRTXExecutionProvider");
        if (device is null)
        {
            Assert.Skip("needs the TensorRT-RTX provider Windows ML installs for an NVIDIA RTX GPU");
        }

        ProviderSelection selection = new(ExecutionProvider.WindowsML, device.AdapterIn(GpuAdapters.Enumerate()), null, device.EpName);
        string model = Model();
        EngineCache cache = new(folder);

        Stopwatch clock = Stopwatch.StartNew();
        using (InferenceSession compiled = cache.Open(model, selection))
        {
            Run(compiled);
        }

        TimeSpan build = clock.Elapsed;
        string[] engines = Directory.GetFiles(folder, "*_ctx.onnx");
        Assert.Single(engines);

        clock.Restart();
        using (InferenceSession loaded = cache.Open(model, selection))
        {
            Run(loaded);
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"build {build.TotalMilliseconds:0} ms, load {clock.Elapsed.TotalMilliseconds:0} ms");
        Assert.Equal(engines, Directory.GetFiles(folder, "*_ctx.onnx"));
        Assert.Empty(Directory.GetFiles(folder, "*.partial.onnx"));
    }

    [Fact]
    public void AModelTheProviderCannotBuildIsSaidOnceAndRunsOnDirectML()
    {
        // SCUNet: TensorRT-RTX 0.4 builds no engine for it (ShapeInferenceNotRegistered).
        WindowsMLDevice? device = WindowsMLProviders.Devices().FirstOrDefault(d => d.EpName == "NvTensorRTRTXExecutionProvider");
        string scunet = Path.Combine(Repository.Root, ".cache", "models", "scunet-color-real-psnr", "fp16.onnx");
        if (device is null || !File.Exists(scunet))
        {
            Assert.Skip("needs the TensorRT-RTX provider and the exported SCUNet");
        }

        ProviderSelection selection = new(ExecutionProvider.WindowsML, device.AdapterIn(GpuAdapters.Enumerate()), null, device.EpName);
        EngineCache cache = new(folder);

        Assert.Throws<NotSupportedException>(() => cache.Open(scunet, selection).Dispose());
        Assert.True(cache.Has(scunet, selection));
        Stopwatch clock = Stopwatch.StartNew();
        Assert.Throws<NotSupportedException>(() => cache.Open(scunet, selection).Dispose());
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"the second refusal took {clock.Elapsed}");

        using SessionCache<InferenceSession> sessions = ModelSessions.Create(_ => scunet, selection, SessionCache<InferenceSession>.DefaultIdle, cache);
        using SessionLease<InferenceSession> lease = sessions.Acquire(new ModelKey("scunet-color-real-psnr", "fp16"));
        Assert.NotNull(lease.Session);
    }

    private string Model()
    {
        string model = Path.Combine(folder, "probe", "fp16.onnx");
        Directory.CreateDirectory(Path.GetDirectoryName(model)!);
        File.WriteAllBytes(model, ProbeModel.Build());
        return model;
    }

    private static void Run(InferenceSession session)
    {
        DenseTensor<float> input = new([1, 3, 16, 16]);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
            session.Run([NamedOnnxValue.CreateFromTensor(session.InputNames[0], input)]);
        Assert.Single(results);
    }
}
