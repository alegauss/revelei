using Revelei.Imaging;

namespace Revelei.Inference;

/// <summary>
/// Tells an allocation failure or a lost GPU apart from any other error ONNX Runtime throws
/// (PB21), so the tiler retries the first two and lets the rest through. ONNX Runtime surfaces all
/// of them as one exception type, so the HRESULT or allocator message is what tells them apart.
/// </summary>
public static class MemoryFailures
{
    // DXGI_ERROR_DEVICE_REMOVED, DXGI_ERROR_DEVICE_HUNG, DXGI_ERROR_DEVICE_RESET.
    private static readonly string[] DeviceLost = ["887A0005", "887A0006", "887A0007"];

    // E_OUTOFMEMORY, D3D's out of video memory, and the allocators' own messages.
    private static readonly string[] OutOfMemory =
    [
        "8007000E",
        "887A0021",
        "out of memory",
        "Failed to allocate memory",
        "bad allocation",
        "bad_alloc",
        "Not enough memory",
    ];

    /// <summary>Classifies a tile's exception.</summary>
    /// <param name="failure">What the model threw.</param>
    /// <returns>Out of memory, device lost, or other.</returns>
    public static TileFailure Classify(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure is OutOfMemoryException or InsufficientMemoryException)
        {
            return TileFailure.OutOfMemory;
        }

        string message = failure.Message;
        if (DeviceLost.Any(code => message.Contains(code, StringComparison.OrdinalIgnoreCase)))
        {
            return TileFailure.DeviceLost;
        }

        return OutOfMemory.Any(text => message.Contains(text, StringComparison.OrdinalIgnoreCase))
            ? TileFailure.OutOfMemory
            : TileFailure.Other;
    }
}
