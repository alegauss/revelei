using Vortice.DXGI;

namespace Revelei.Inference;

/// <summary>A hardware GPU as DXGI lists it.</summary>
/// <param name="Index">Its position in EnumAdapters1, which is the device id DirectML takes.</param>
/// <param name="Name">Its description, e.g. NVIDIA GeForce RTX 4060.</param>
/// <param name="DedicatedVideoMemory">Bytes of its own video memory.</param>
/// <param name="VendorId">Its PCI vendor id, which matches it to a Windows ML provider's device (PB43).</param>
public sealed record GpuAdapter(int Index, string Name, long DedicatedVideoMemory, uint VendorId = 0)
{
    /// <summary>The name and memory as the status bar shows them, e.g. "NVIDIA GeForce RTX 4060, 8 GB".</summary>
    public string Label => $"{Name}, {Math.Round(DedicatedVideoMemory / (1024.0 * 1024 * 1024), 1)} GB";
}

/// <summary>Lists the GPUs DirectML can run on (PB14).</summary>
public static class GpuAdapters
{
    /// <summary>
    /// Every hardware adapter in EnumAdapters1 order, skipping software ones (WARP, the Basic
    /// Render Driver). The index is kept as DXGI gives it, because DirectML's device id is that
    /// index: on the PB14 desk device 0 ran Real-ESRGAN 19 times faster than device 1, matching
    /// DXGI's order of the RTX 4060 before the UHD 770.
    /// </summary>
    /// <returns>The adapters, empty when DXGI cannot be reached.</returns>
    public static IReadOnlyList<GpuAdapter> Enumerate()
    {
        List<GpuAdapter> found = [];
        if (DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory).Failure || factory is null)
        {
            return found;
        }

        using (factory)
        {
            for (uint index = 0; factory.EnumAdapters1(index, out IDXGIAdapter1? adapter).Success; index++)
            {
                using (adapter)
                {
                    AdapterDescription1 description = adapter.Description1;
                    if ((description.Flags & AdapterFlags.Software) == 0)
                    {
                        // Through nuint: PointerUSize's implicit conversion to uint overflows past 4 GB.
                        nuint memory = description.DedicatedVideoMemory;
                        found.Add(new GpuAdapter((int)index, description.Description, (long)memory, description.VendorId));
                    }
                }
            }
        }

        return found;
    }

    /// <summary>The adapter with the most dedicated video memory, which is the one to infer on.</summary>
    /// <param name="adapters">What <see cref="Enumerate"/> found.</param>
    /// <returns>The best adapter, or null when there is none.</returns>
    public static GpuAdapter? Best(IEnumerable<GpuAdapter> adapters) =>
        adapters.OrderByDescending(adapter => adapter.DedicatedVideoMemory).ThenBy(adapter => adapter.Index).FirstOrDefault();
}
