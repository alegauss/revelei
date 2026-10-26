namespace Revelei.Inference.Tests;

/// <summary>
/// PB14: the provider is DirectML on the GPU with the most video memory when the probe runs
/// there, CPU otherwise, with the reason kept for the status bar. Never a crash for a GPU.
/// PB43: a provider Windows ML installed for this PC comes before DirectML, on its vendor's GPU.
/// </summary>
public sealed class ProviderDetectorTests
{
    private const uint Nvidia = 0x10DE;
    private const uint Intel = 0x8086;
    private static readonly GpuAdapter Integrated = new(1, "Intel UHD 770", 128L << 20, Intel);
    private static readonly GpuAdapter Discrete = new(0, "NVIDIA RTX 4060", 8L << 30, Nvidia);
    private static readonly WindowsMLDevice TensorRT = new("NvTensorRTRTXExecutionProvider", Nvidia);
    private static readonly WindowsMLDevice OpenVINO = new("OpenVINOExecutionProvider", Intel);

    [Fact]
    public void AutoPicksDirectMLOnTheAdapterWithTheMostVideoMemory()
    {
        FakeProbe probe = new(directMLWorks: true);

        ProviderSelection selection = ProviderDetector.Detect(ProviderPreference.Auto, [Integrated, Discrete], [], probe);

        Assert.Equal(ExecutionProvider.DirectML, selection.Provider);
        Assert.Equal(Discrete, selection.Adapter);
        Assert.Null(selection.Fallback);
        Assert.Equal("DirectML · NVIDIA RTX 4060, 8 GB", selection.Label);
    }

    [Fact]
    public void AFailingGpuFallsBackToCpuWithTheReason()
    {
        FakeProbe probe = new(directMLWorks: false);

        ProviderSelection selection = ProviderDetector.Detect(ProviderPreference.Auto, [Discrete], [], probe);

        Assert.Equal(ExecutionProvider.Cpu, selection.Provider);
        Assert.Equal("887A0004", selection.Fallback);
        Assert.Equal("CPU (GPU unavailable: 887A0004)", selection.Label);
    }

    [Fact]
    public void NoGpuFallsBackToCpuAndSaysSo()
    {
        ProviderSelection selection = ProviderDetector.Detect(ProviderPreference.DirectML, [], [], new FakeProbe(directMLWorks: true));

        Assert.Equal(ExecutionProvider.Cpu, selection.Provider);
        Assert.Equal("no DirectX 12 GPU found", selection.Fallback);
    }

    [Fact]
    public void CpuPreferenceNeverTriesTheGpu()
    {
        FakeProbe probe = new(directMLWorks: true, windowsMLWorks: true);

        ProviderSelection selection = ProviderDetector.Detect(ProviderPreference.Cpu, [Discrete], [TensorRT], probe);

        Assert.Equal(ExecutionProvider.Cpu, selection.Provider);
        Assert.Equal("CPU", selection.Label);
        Assert.Equal([ExecutionProvider.Cpu], probe.Tried);
    }

    [Fact]
    public void ACpuThatCannotRunIsAnError() =>
        Assert.Throws<InvalidOperationException>(() =>
            ProviderDetector.Detect(ProviderPreference.Cpu, [], [], new FakeProbe(directMLWorks: false, cpuWorks: false)));

    [Fact]
    public void AutoAddsTensorRtForTheUpscalersOnlyAndOnlyFromBuiltEngines()
    {
        // PB52: a 4x upscale of a 933 x 1405 photo took 5.4 s on TensorRT-RTX against 47 to 67 s on
        // DirectML; RMBG ran three times slower there, so every other model stays on DirectML.
        FakeProbe probe = new(directMLWorks: true, windowsMLWorks: true);

        ProviderSelection selection = ProviderDetector.Detect(ProviderPreference.Auto, [Integrated, Discrete], [TensorRT], probe);

        Assert.Equal(ExecutionProvider.WindowsML, selection.Provider);
        Assert.True(selection.EnginesReadyOnly);
        Assert.Equal(Discrete, selection.Adapter);
        Assert.Equal("DirectML · NVIDIA RTX 4060, 8 GB · TensorRT-RTX for upscaling", selection.Label);
        Assert.Equal(ExecutionProvider.WindowsML, selection.For("realesrgan-x4plus").Provider);
        Assert.Equal(new ProviderSelection(ExecutionProvider.DirectML, Discrete, null), selection.For("rmbg-1.4"));
    }

    [Fact]
    public void AutoStaysOnDirectMLWithoutTensorRtOnItsGpu()
    {
        // OpenVINO is unmeasured, and a TensorRT-RTX on another GPU than DirectML's is not used.
        FakeProbe probe = new(directMLWorks: true, windowsMLWorks: true);
        WindowsMLDevice elsewhere = new(TensorRT.EpName, Nvidia, 5);

        Assert.Equal(ExecutionProvider.DirectML, ProviderDetector.Detect(ProviderPreference.Auto, [Discrete], [OpenVINO], probe).Provider);
        Assert.Equal(ExecutionProvider.DirectML, ProviderDetector.Detect(ProviderPreference.Auto, [Discrete], [elsewhere], probe).Provider);
        Assert.Equal(ExecutionProvider.DirectML, ProviderDetector.Detect(ProviderPreference.Auto, [Discrete], [TensorRT], new FakeProbe(directMLWorks: true)).Provider);
    }

    [Fact]
    public void TheWindowsMLPreferenceRunsEveryModelOnItsProvider()
    {
        ProviderSelection selection = ProviderDetector.Detect(ProviderPreference.WindowsML, [Discrete], [TensorRT], new FakeProbe(directMLWorks: true, windowsMLWorks: true));

        Assert.False(selection.EnginesReadyOnly);
        Assert.Same(selection, selection.For("rmbg-1.4"));
    }

    [Fact]
    public void WindowsMLPreferencePicksAProviderOnItsVendorsGpu()
    {
        FakeProbe probe = new(directMLWorks: true, windowsMLWorks: true);

        ProviderSelection selection = ProviderDetector.Detect(ProviderPreference.WindowsML, [Integrated, Discrete], [TensorRT, OpenVINO], probe);

        Assert.Equal(ExecutionProvider.WindowsML, selection.Provider);
        Assert.Equal("NvTensorRTRTXExecutionProvider", selection.Accelerator);
        Assert.Equal(Discrete, selection.Adapter);
        Assert.Equal("TensorRT-RTX", selection.Name);
        Assert.Equal("TensorRT-RTX · NVIDIA RTX 4060, 8 GB", selection.Label);
        Assert.DoesNotContain(ExecutionProvider.DirectML, probe.Tried);
    }

    [Fact]
    public void AWindowsMLProviderThatFailsLeavesDirectMLAsItWas()
    {
        // PB43: asked for Windows ML, DirectML still runs wherever Windows ML has nothing that does.
        FakeProbe probe = new(directMLWorks: true, windowsMLWorks: false);

        ProviderSelection selection = ProviderDetector.Detect(ProviderPreference.WindowsML, [Discrete], [TensorRT], probe);

        Assert.Equal(ExecutionProvider.DirectML, selection.Provider);
        Assert.Null(selection.Fallback);
        Assert.Equal([ExecutionProvider.WindowsML, ExecutionProvider.DirectML], probe.Tried);
    }

    [Fact]
    public void DirectMLPreferenceNeverTriesWindowsML()
    {
        FakeProbe probe = new(directMLWorks: true, windowsMLWorks: true);

        ProviderSelection selection = ProviderDetector.Detect(ProviderPreference.DirectML, [Discrete], [TensorRT], probe);

        Assert.Equal(ExecutionProvider.DirectML, selection.Provider);
        Assert.DoesNotContain(ExecutionProvider.WindowsML, probe.Tried);
    }

    [Fact]
    public void WindowsMLPreferenceWithNoProviderDoesWhatAutoDoes()
    {
        ProviderSelection onGpu = ProviderDetector.Detect(ProviderPreference.WindowsML, [Discrete], [], new FakeProbe(directMLWorks: true));
        ProviderSelection noGpu = ProviderDetector.Detect(ProviderPreference.WindowsML, [], [], new FakeProbe(directMLWorks: true));

        Assert.Equal(ExecutionProvider.DirectML, onGpu.Provider);
        Assert.Equal(ExecutionProvider.Cpu, noGpu.Provider);
        Assert.Equal("no DirectX 12 GPU found", noGpu.Fallback);
    }

    [Fact]
    public void AWindowsMLProviderOnAGpuDxgiDoesNotListIsNamedWithoutOne()
    {
        FakeProbe probe = new(directMLWorks: true, windowsMLWorks: true);

        ProviderSelection selection = ProviderDetector.Detect(ProviderPreference.WindowsML, [Discrete], [OpenVINO], probe);

        Assert.Equal(ExecutionProvider.WindowsML, selection.Provider);
        Assert.Null(selection.Adapter);
        Assert.Equal("OpenVINO", selection.Label);
    }

    [Fact]
    public void RankingPutsTensorRTFirstAndLeavesTheBuiltInProvidersOut()
    {
        WindowsMLDevice other = new("ContosoExecutionProvider", 0x1234);
        IReadOnlyList<WindowsMLDevice> ranked = WindowsMLProviders.Rank(
            [new("DmlExecutionProvider", Nvidia), other, OpenVINO, new("OpenVINOExecutionProvider.AUTO", Intel), TensorRT, TensorRT, new("CPUExecutionProvider", 0)]);

        Assert.Equal([TensorRT, OpenVINO, other], ranked);
        Assert.Equal("Contoso", WindowsMLDevice.ShortName(other.EpName));
    }

    [Fact]
    public void ADeviceWithAnAdapterIndexRunsOnThatAdapterAndNotOnItsVendorsBiggest()
    {
        GpuAdapter second = new(2, "NVIDIA RTX 3050", 4L << 30, Nvidia);

        Assert.Equal(second, new WindowsMLDevice(TensorRT.EpName, Nvidia, 2).AdapterIn([Discrete, second]));
        Assert.Equal(Discrete, TensorRT.AdapterIn([Discrete, second]));
    }

    [Theory]
    [InlineData("auto", ProviderPreference.Auto)]
    [InlineData("dml", ProviderPreference.DirectML)]
    [InlineData("DirectML", ProviderPreference.DirectML)]
    [InlineData("winml", ProviderPreference.WindowsML)]
    [InlineData(" cpu ", ProviderPreference.Cpu)]
    public void ProviderFlagValuesParse(string value, ProviderPreference expected)
    {
        Assert.True(ProviderDetector.TryParse(value, out ProviderPreference preference));
        Assert.Equal(expected, preference);
    }

    [Theory]
    [InlineData("cuda")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownProviderFlagValuesAreRefused(string? value) =>
        Assert.False(ProviderDetector.TryParse(value, out _));

    [Fact]
    public void TheRealProbeRunsOnCpu()
    {
        Assert.True(new OnnxRuntimeProbe().TryRun(new ProviderSelection(ExecutionProvider.Cpu, null, null), out string? error), error);
    }

    [Fact]
    public void AWindowsMLProviderNotRegisteredIsAFailureNotAThrow()
    {
        ProviderSelection absent = new(ExecutionProvider.WindowsML, null, null, "NoSuchExecutionProvider");

        Assert.False(new OnnxRuntimeProbe().TryRun(absent, out string? error));
        Assert.Contains("not registered", error, StringComparison.Ordinal);
    }

    [Fact]
    public void RealDetectionAlwaysAnswers()
    {
        // Whatever this desk has: a GPU that runs, one that does not, or none. Never a throw. On a
        // GPU auto runs DirectML, with TensorRT-RTX for the upscalers where it runs there (PB52).
        ProviderSelection selection = ProviderDetector.Detect(ProviderPreference.Auto);

        Assert.False(string.IsNullOrWhiteSpace(selection.Label));
        Assert.Equal(selection.Provider != ExecutionProvider.Cpu, selection.Adapter is not null);
        Assert.Equal(selection.Provider == ExecutionProvider.WindowsML, selection.EnginesReadyOnly);
    }

    [Fact]
    public void RealWindowsMLDetectionAlwaysAnswers()
    {
        // PB43: a Windows ML provider where this desk has one that runs, else what Auto picks.
        ProviderSelection selection = ProviderDetector.Detect(ProviderPreference.WindowsML);

        Assert.False(string.IsNullOrWhiteSpace(selection.Label));
        Assert.Equal(selection.Provider == ExecutionProvider.WindowsML, selection.Accelerator is not null);
    }

    [Fact]
    public void TheRealWindowsMLCatalogAnswersWithoutThrowing()
    {
        // PB43: an older Windows or a failing catalog reads as "nothing", never as a crash.
        foreach (WindowsMLDevice device in WindowsMLProviders.Devices())
        {
            Assert.EndsWith("ExecutionProvider", device.EpName, StringComparison.Ordinal);
        }

        Assert.NotNull(WindowsMLProviders.Missing());
    }

    [Fact]
    public void EveryListedAdapterIsHardwareWithAName()
    {
        foreach (GpuAdapter adapter in GpuAdapters.Enumerate())
        {
            Assert.False(string.IsNullOrWhiteSpace(adapter.Name));
            Assert.DoesNotContain("Basic Render", adapter.Name, StringComparison.Ordinal);
        }
    }

    private sealed class FakeProbe(bool directMLWorks, bool cpuWorks = true, bool windowsMLWorks = false) : IProviderProbe
    {
        public List<ExecutionProvider> Tried { get; } = [];

        public bool TryRun(ProviderSelection candidate, out string? error)
        {
            Tried.Add(candidate.Provider);
            bool works = candidate.Provider switch
            {
                ExecutionProvider.DirectML => directMLWorks,
                ExecutionProvider.WindowsML => windowsMLWorks,
                _ => cpuWorks,
            };
            error = works ? null : "887A0004";
            return works;
        }
    }
}
