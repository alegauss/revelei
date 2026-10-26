using System.Runtime.InteropServices;
using Vortice.DXGI;

namespace Revelei.Inference;

/// <summary>
/// The starting tile from the memory there is (PB41): the largest side from 128 to 1024 whose input,
/// overlap included, fits the free video memory on a GPU or the available RAM on the CPU. Starting
/// there avoids most out-of-memory retries (PB21) on a small GPU and wastes no speed on a large one;
/// the retry stays as the safety net.
/// </summary>
public static class TileSizer
{
    /// <summary>The smallest and largest starting tile, and the step between.</summary>
    public const int MinTile = 128, MaxTile = 1024, Step = 32;

    /// <summary>
    /// Peak memory per input pixel over meta.json's largest activation. Measured on DirectML with the
    /// RTX 4060 (PB41): Real-ESRGAN x4plus held up to 22,580 B/px against a largest activation of
    /// 4,096 (5.5x), x2plus up to 5,953 against 1,024 (5.8x), the allocator growing in steps.
    /// </summary>
    public const double ActivationMultiplier = 6;

    /// <summary>The share of the free memory a tile may plan to use.</summary>
    public const double Margin = 0.8;

    /// <summary>The largest starting tile that fits.</summary>
    /// <param name="freeBytes">Free video memory, or available RAM on the CPU.</param>
    /// <param name="largestActivationBytesPerPixel">From the model's meta.json.</param>
    /// <param name="overlap">Context read on each side of a tile.</param>
    /// <returns>A side from <see cref="MinTile"/> to <see cref="MaxTile"/>; the minimum when nothing fits.</returns>
    public static int Choose(long freeBytes, double largestActivationBytesPerPixel, int overlap)
    {
        double perPixel = largestActivationBytesPerPixel * ActivationMultiplier;
        double allowed = freeBytes * Margin;
        for (int tile = MaxTile; tile >= MinTile; tile -= Step)
        {
            double input = tile + (2.0 * overlap);
            if (input * input * perPixel <= allowed)
            {
                return tile;
            }
        }

        return MinTile;
    }
}

/// <summary>How much memory a run may use, where it runs.</summary>
public static class MemoryBudget
{
    /// <summary>Free bytes: DXGI's local budget less its current usage on the GPU, available physical RAM on the CPU.</summary>
    /// <param name="provider">Where inference runs.</param>
    /// <returns>Bytes; 0 when it cannot be read.</returns>
    public static long Free(ProviderSelection provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider.Provider != ExecutionProvider.Cpu && provider.Adapter is { } adapter ? FreeVideoMemory(adapter.Index) : AvailableRam();
    }

    private static long FreeVideoMemory(int index)
    {
        if (DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory).Failure || factory is null)
        {
            return 0;
        }

        using (factory)
        {
            if (factory.EnumAdapters1((uint)index, out IDXGIAdapter1? adapter).Failure || adapter is null)
            {
                return 0;
            }

            using (adapter)
            {
                // PB56: VMware's SVGA adapter answers 0x8876086A instead of a budget, and a driver
                // without IDXGIAdapter3 cannot be asked at all. Either is "unknown", which
                // TiledModel answers with meta.json's recommended tile rather than a crash.
                try
                {
                    using IDXGIAdapter3 adapter3 = adapter.QueryInterface<IDXGIAdapter3>();
                    QueryVideoMemoryInfo info = adapter3.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
                    return Math.Max(0, (long)info.Budget - (long)info.CurrentUsage);
                }
                catch (SharpGen.Runtime.SharpGenException)
                {
                    return 0;
                }
            }
        }
    }

    private static long AvailableRam()
    {
        MemoryStatus status = new() { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? (long)status.AvailablePhysical : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
