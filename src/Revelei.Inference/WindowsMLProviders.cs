using System.Globalization;
using Microsoft.ML.OnnxRuntime;
using WinML = Microsoft.Windows.AI.MachineLearning;

namespace Revelei.Inference;

/// <summary>A provider Windows ML registered with ONNX Runtime, on one GPU.</summary>
/// <param name="EpName">Its ONNX Runtime name, e.g. NvTensorRTRTXExecutionProvider.</param>
/// <param name="VendorId">The PCI vendor id of the GPU it runs on, which DXGI reports too.</param>
/// <param name="AdapterIndex">The GPU's EnumAdapters1 index, when ONNX Runtime's device metadata gives it.</param>
public sealed record WindowsMLDevice(string EpName, uint VendorId, int? AdapterIndex = null)
{
    /// <summary>The name the status bar and the log use, e.g. TensorRT-RTX.</summary>
    /// <param name="epName">The ONNX Runtime name.</param>
    /// <returns>The short name.</returns>
    public static string ShortName(string epName)
    {
        ArgumentNullException.ThrowIfNull(epName);
        return epName switch
        {
            "NvTensorRTRTXExecutionProvider" => "TensorRT-RTX",
            "OpenVINOExecutionProvider" => "OpenVINO",
            _ when epName.EndsWith("ExecutionProvider", StringComparison.Ordinal) => epName[..^"ExecutionProvider".Length],
            _ => epName,
        };
    }

    /// <summary>
    /// The GPU this provider runs on: the adapter at its index, or without one the adapter from its
    /// vendor with the most video memory.
    /// </summary>
    /// <param name="adapters">What DXGI lists.</param>
    /// <returns>The adapter, or null when DXGI lists none that matches.</returns>
    public GpuAdapter? AdapterIn(IEnumerable<GpuAdapter> adapters) => GpuAdapters.Best(adapters.Where(Matches));

    /// <summary>Whether this device runs on the given GPU.</summary>
    /// <param name="adapter">A DXGI adapter.</param>
    /// <returns>True for its adapter.</returns>
    public bool Matches(GpuAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        return AdapterIndex is { } index ? adapter.Index == index : adapter.VendorId == VendorId;
    }

    /// <summary>The device as ONNX Runtime lists it.</summary>
    /// <param name="device">An EP device.</param>
    /// <returns>Its name, vendor and adapter index.</returns>
    internal static WindowsMLDevice From(OrtEpDevice device)
    {
        OrtHardwareDevice hardware = device.HardwareDevice;
        int? index = hardware.Metadata.Entries.TryGetValue("DxgiAdapterNumber", out string? number)
            && int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;
        return new WindowsMLDevice(device.EpName, hardware.VendorId, index);
    }
}

/// <summary>
/// The execution providers Windows ML installs for this PC (PB43), NVIDIA TensorRT-RTX and Intel
/// OpenVINO, used when --provider winml asks for them. Windows Update delivers them, which took 57
/// s on the PB5 spike's first run, so startup only registers what is already on disk and
/// <see cref="AcquireAsync"/> fetches the rest in the background for the next start. Everything
/// here answers "none" rather than throwing: an older Windows without reg-free WinRT, or a catalog
/// that fails, leaves DirectML and CPU exactly as they were.
/// </summary>
/// <remarks>
/// <para>
/// Not the default, measured on an RTX 4060 with Real-ESRGAN x4 on 0014.jpg: TensorRT-RTX runs a
/// tile in 112 ms against DirectML's 324 ms at FP16 (and 252 against 232 ms at FP32), but builds
/// its engine for 10 to 17 s whenever a session is created, so the first upscale took 16 to 20 s
/// against DirectML's 3 to 6 s, and PB16 releases an idle session after 5 minutes. Its runtime
/// cache cut a new tile shape from 3.7 s to 0.8 s and left the session build as it was.
/// <see cref="EngineCache"/> (PB52) keeps the built engine as an EP context model: the first
/// upscale in a later process took 1.8 s against 8.1 s, with DirectML at 2.0 s.
/// </para>
/// <para>
/// The device is chosen here, not by SetEpSelectionPolicy(PREFER_GPU) as in the spike: the policy
/// cannot say which provider it picked, and the status bar and the log must name it. TensorRT-RTX
/// came within 57.7 dB (FP16) and 60.5 dB (FP32) PSNR of CPU on that upscale, far above PB10's
/// lowest INT8 floor of 27 dB.
/// </para>
/// </remarks>
public static class WindowsMLProviders
{
    // GPU providers by how well the spike saw them run, best first; one not named here comes last.
    private static readonly string[] Ranked = ["NvTensorRTRTXExecutionProvider", "OpenVINOExecutionProvider"];

    // Providers ONNX Runtime has without Windows ML; DirectML is tried on its own path.
    private static readonly string[] BuiltIn = ["CPUExecutionProvider", "DmlExecutionProvider"];

    private static readonly Lock Gate = new();
    private static bool registered;

    /// <summary>
    /// Registers the certified providers already on disk, once per process, and lists the GPU ones
    /// ONNX Runtime can now use, best first. Fetches nothing.
    /// </summary>
    /// <returns>The devices; empty when Windows ML has none or cannot be reached.</returns>
    public static IReadOnlyList<WindowsMLDevice> Devices()
    {
        try
        {
            lock (Gate)
            {
                if (!registered)
                {
                    WinML.ExecutionProviderCatalog.GetDefault().RegisterCertifiedAsync().AsTask().GetAwaiter().GetResult();
                    registered = true;
                }
            }

            return Rank(OrtEnv.Instance().GetEpDevices()
                .Where(device => device.HardwareDevice.Type == OrtHardwareDeviceType.GPU)
                .Select(WindowsMLDevice.From));
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            return [];
        }
    }

    /// <summary>
    /// Puts the built-in providers aside, and a provider's device-plugin variants such as
    /// OpenVINOExecutionProvider.AUTO, drops duplicates and orders the rest best first.
    /// </summary>
    /// <param name="devices">What ONNX Runtime lists.</param>
    /// <returns>The candidates, best first.</returns>
    public static IReadOnlyList<WindowsMLDevice> Rank(IEnumerable<WindowsMLDevice> devices) =>
        [.. devices
            .Where(device => !BuiltIn.Contains(device.EpName, StringComparer.Ordinal) && !device.EpName.Contains('.', StringComparison.Ordinal))
            .Distinct()
            .OrderBy(device => Array.IndexOf(Ranked, device.EpName) is int rank and >= 0 ? rank : Ranked.Length)
            .ThenBy(device => device.EpName, StringComparer.Ordinal)];

    /// <summary>
    /// The certified providers this PC could use that are not on disk yet. NotReady is not one: it
    /// means on disk and not yet registered in this process, which <see cref="Devices"/> does.
    /// </summary>
    /// <returns>Their ONNX Runtime names; empty when there are none or the catalog cannot be read.</returns>
    public static IReadOnlyList<string> Missing()
    {
        try
        {
            return [.. Absent().Select(provider => provider.Name)];
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            return [];
        }
    }

    /// <summary>
    /// Fetches the certified providers not on disk yet, through Windows Update, without registering
    /// them: the sessions of this run are already on DirectML or CPU, and the next start picks them up.
    /// </summary>
    /// <returns>Why a provider did not arrive, or null when every one did.</returns>
    public static async Task<string?> AcquireAsync()
    {
        try
        {
            foreach (WinML.ExecutionProvider provider in Absent())
            {
                WinML.ExecutionProviderReadyResult result = await provider.EnsureReadyAsync().AsTask().ConfigureAwait(false);
                if (result.Status != WinML.ExecutionProviderReadyResultState.Success)
                {
                    return $"{WindowsMLDevice.ShortName(provider.Name)}: {result.DiagnosticText}";
                }
            }

            return null;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            return failure.Message;
        }
    }

    /// <summary>Adds the selection's Windows ML provider, on its GPU, to the session options.</summary>
    /// <param name="options">The options a session is built from.</param>
    /// <param name="selection">A Windows ML selection.</param>
    /// <exception cref="InvalidOperationException">The provider is not registered in this process.</exception>
    internal static void Append(SessionOptions options, ProviderSelection selection)
    {
        OrtEnv env = OrtEnv.Instance();
        List<OrtEpDevice> devices = [.. env.GetEpDevices().Where(device =>
            device.EpName == selection.Accelerator
            && device.HardwareDevice.Type == OrtHardwareDeviceType.GPU
            && (selection.Adapter is null || WindowsMLDevice.From(device).Matches(selection.Adapter)))];
        if (devices.Count == 0)
        {
            throw new InvalidOperationException($"{selection.Name} is not registered with ONNX Runtime.");
        }

        options.AppendExecutionProvider(env, devices, new Dictionary<string, string>());
    }

    private static IEnumerable<WinML.ExecutionProvider> Absent() =>
        WinML.ExecutionProviderCatalog.GetDefault().FindAllProviders()
            .Where(provider => provider.Certification == WinML.ExecutionProviderCertification.Certified
                && provider.ReadyState == WinML.ExecutionProviderReadyState.NotPresent);
}
