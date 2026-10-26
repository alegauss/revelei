using Revelei.Imaging;

namespace Revelei.Inference.Tests;

/// <summary>PB21: which ONNX Runtime failures are worth a smaller tile, and which need another device.</summary>
public sealed class MemoryFailuresTests
{
    [Theory]
    [InlineData("D:\\a\\_work\\dml\\DmlCommittedResourceAllocator.cpp(22)\\onnxruntime.dll: (caller) Exception(3) 8007000E Not enough memory resources are available")]
    [InlineData("[ErrorCode:RuntimeException] 887A0021 out of video memory")]
    [InlineData("[ErrorCode:Fail] Failed to allocate memory for requested buffer of size 805306368")]
    [InlineData("bad allocation")]
    [InlineData("CUDA failure 2: out of memory")]
    public void AllocationFailuresAreOutOfMemory(string message) =>
        Assert.Equal(TileFailure.OutOfMemory, MemoryFailures.Classify(new InvalidOperationException(message)));

    [Theory]
    [InlineData("887A0005 The GPU device instance has been suspended")]
    [InlineData("Exception(1) tid(1a2b) 887A0006 hung")]
    public void ALostDeviceIsDeviceLost(string message) =>
        Assert.Equal(TileFailure.DeviceLost, MemoryFailures.Classify(new InvalidOperationException(message)));

    [Fact]
    public void TheRuntimesOwnOutOfMemoryCounts() =>
        Assert.Equal(TileFailure.OutOfMemory, MemoryFailures.Classify(new OutOfMemoryException()));

    [Theory]
    [InlineData("Invalid input name: x")]
    [InlineData("887A0004 not supported")]
    public void EverythingElseIsNotRetried(string message) =>
        Assert.Equal(TileFailure.Other, MemoryFailures.Classify(new InvalidOperationException(message)));

    [Fact]
    public void ACpuLibraryHasNoFallbackAndAGpuOneDoes()
    {
        string root = Path.GetTempPath();
        using ModelLibrary cpu = new(root, new ProviderSelection(ExecutionProvider.Cpu, null, null));
        using ModelLibrary gpu = new(root, new ProviderSelection(ExecutionProvider.DirectML, new GpuAdapter(0, "GPU", 1L << 30), null));

        Assert.Null(cpu.CpuFallback);
        Assert.NotNull(gpu.CpuFallback);
    }
}
