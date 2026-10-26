using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Revelei.Inference;

/// <summary>Where inference runs.</summary>
public enum ExecutionProvider
{
    /// <summary>ONNX Runtime's CPU provider, always available.</summary>
    Cpu,

    /// <summary>DirectML on a DX12 GPU, which Windows ML's ONNX Runtime carries (PB5).</summary>
    DirectML,

    /// <summary>A provider Windows ML installed for this PC's GPU, e.g. TensorRT-RTX or OpenVINO (PB43).</summary>
    WindowsML,
}

/// <summary>What the user or a command-line flag asked for.</summary>
public enum ProviderPreference
{
    /// <summary>
    /// DirectML, then CPU: the default, which needs nothing from Windows Update (PB43). Where
    /// TensorRT-RTX runs, the upscalers use it once their engines are built (PB52).
    /// </summary>
    Auto,

    /// <summary>DirectML, falling back to CPU only if it cannot run.</summary>
    DirectML,

    /// <summary>CPU, without trying a GPU.</summary>
    Cpu,

    /// <summary>A Windows ML provider (PB43), else what Auto picks.</summary>
    WindowsML,
}

/// <summary>The provider chosen, and why the others were not.</summary>
/// <param name="Provider">The provider every session will use.</param>
/// <param name="Adapter">The GPU it runs on, when known.</param>
/// <param name="Fallback">Why a GPU was asked for and not used, or null.</param>
/// <param name="Accelerator">The Windows ML provider's ONNX Runtime name, for <see cref="ExecutionProvider.WindowsML"/>.</param>
public sealed record ProviderSelection(ExecutionProvider Provider, GpuAdapter? Adapter, string? Fallback, string? Accelerator = null)
{
    /// <summary>
    /// PB52: the only models the Windows ML provider runs; every other one runs on DirectML on
    /// the same GPU. Null runs every model on it, as --provider winml does.
    /// </summary>
    public IReadOnlySet<string>? AcceleratedModels { get; init; }

    /// <summary>
    /// PB52: a model runs on the Windows ML provider only once its engine is built, and on
    /// DirectML until then, so no run pays a 10 to 17 s build. Auto sets it; --provider winml does not.
    /// </summary>
    public bool EnginesReadyOnly { get; init; }

    /// <summary>The provider as the log and the status bar name it: DirectML, Cpu, or e.g. TensorRT-RTX.</summary>
    public string Name => Provider == ExecutionProvider.WindowsML && Accelerator is not null
        ? WindowsMLDevice.ShortName(Accelerator)
        : Provider.ToString();

    /// <summary>
    /// What the status bar shows, e.g. "DirectML · NVIDIA GeForce RTX 4060, 8 GB", or with auto's
    /// accelerated upscalers "DirectML · NVIDIA GeForce RTX 4060, 8 GB · TensorRT-RTX for upscaling".
    /// </summary>
    public string Label => Provider switch
    {
        ExecutionProvider.Cpu => Fallback is null ? "CPU" : $"CPU (GPU unavailable: {Fallback})",
        ExecutionProvider.WindowsML when AcceleratedModels is not null && Adapter is not null =>
            $"DirectML · {Adapter.Label} · {Name} for upscaling",
        _ when Adapter is not null => $"{Name} · {Adapter.Label}",
        _ => Name,
    };

    /// <summary>The provider a model's session runs on, as a selection of its own.</summary>
    /// <param name="model">The model's folder name.</param>
    /// <returns>This selection, or DirectML on the same GPU for a model it does not accelerate.</returns>
    public ProviderSelection For(string model) =>
        Provider == ExecutionProvider.WindowsML && AcceleratedModels is { } only && !only.Contains(model)
            ? OnDirectML()
            : this;

    /// <summary>DirectML on this selection's GPU, or the CPU when it has none.</summary>
    /// <returns>The fallback selection.</returns>
    public ProviderSelection OnDirectML() => Adapter is { } adapter
        ? new ProviderSelection(ExecutionProvider.DirectML, adapter, null)
        : new ProviderSelection(ExecutionProvider.Cpu, null, null);
}

/// <summary>Runs the probe model on one provider; true when it ran, with the failure otherwise.</summary>
public interface IProviderProbe
{
    /// <summary>Tries a provider.</summary>
    /// <param name="candidate">The provider, its GPU and, for Windows ML, its accelerator.</param>
    /// <param name="error">Why it failed, when it did.</param>
    /// <returns>Whether a session ran the probe model.</returns>
    bool TryRun(ProviderSelection candidate, out string? error);
}

/// <summary>
/// Picks the execution provider once at startup (PB14): DirectML on the GPU with the most video
/// memory when a probe session runs there, else CPU. Asked for Windows ML (PB43), a provider it has
/// already installed for this PC comes first. Never throws for a GPU that will not run; that is a
/// fallback with its reason, shown to the user.
/// </summary>
public static class ProviderDetector
{
    /// <summary>The Windows ML provider auto uses, the one measured (PB52).</summary>
    public const string AutoAccelerator = "NvTensorRTRTXExecutionProvider";

    /// <summary>
    /// The models auto runs on <see cref="AutoAccelerator"/>, measured on the RTX 4060 (PB52): a
    /// 4x upscale of a 933 x 1405 photo took 5.4 s warm against DirectML's 47 to 67 s, while RMBG
    /// ran three times slower (393 against 126 ms) and GFPGAN gained little. x2plus builds no
    /// engine there yet (PB55) and so runs on DirectML until it does.
    /// </summary>
    public static readonly IReadOnlySet<string> AutoAcceleratedModels = new HashSet<string>(StringComparer.Ordinal)
    {
        "realesrgan-x4plus",
        "realesrgan-x2plus",
    };

    /// <summary>Detects with the real GPUs, the Windows ML providers already present and the real probe.</summary>
    /// <param name="preference">What was asked for.</param>
    /// <returns>The selection.</returns>
    public static ProviderSelection Detect(ProviderPreference preference) =>
        Detect(
            preference,
            GpuAdapters.Enumerate(),
            preference is ProviderPreference.WindowsML or ProviderPreference.Auto ? WindowsMLProviders.Devices() : [],
            new OnnxRuntimeProbe());

    /// <summary>Detects against the given GPUs, Windows ML devices and probe.</summary>
    /// <param name="preference">What was asked for.</param>
    /// <param name="adapters">The GPUs to consider.</param>
    /// <param name="accelerators">The Windows ML providers registered with ONNX Runtime, best first.</param>
    /// <param name="probe">How a provider is tried.</param>
    /// <returns>The selection.</returns>
    public static ProviderSelection Detect(
        ProviderPreference preference, IReadOnlyList<GpuAdapter> adapters, IReadOnlyList<WindowsMLDevice> accelerators, IProviderProbe probe)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(accelerators);
        ArgumentNullException.ThrowIfNull(probe);

        string? fallback = null;
        if (preference == ProviderPreference.WindowsML)
        {
            foreach (WindowsMLDevice device in accelerators)
            {
                ProviderSelection candidate = new(ExecutionProvider.WindowsML, device.AdapterIn(adapters), null, device.EpName);
                if (probe.TryRun(candidate, out _))
                {
                    return candidate;
                }
            }
        }

        if (preference != ProviderPreference.Cpu)
        {
            GpuAdapter? best = GpuAdapters.Best(adapters);
            ProviderSelection directML = new(ExecutionProvider.DirectML, best, null);
            if (best is null)
            {
                fallback = "no DirectX 12 GPU found";
            }
            else if (probe.TryRun(directML, out string? error))
            {
                // PB52: auto adds TensorRT-RTX for the upscalers, on the same GPU DirectML runs
                // on, so every model it does not accelerate stays exactly where it was.
                return preference == ProviderPreference.Auto && Accelerated(accelerators, adapters, best, probe) is { } accelerated
                    ? accelerated
                    : directML;
            }
            else
            {
                fallback = error ?? "DirectML did not run";
            }
        }

        return probe.TryRun(new ProviderSelection(ExecutionProvider.Cpu, null, null), out string? cpuError)
            ? new ProviderSelection(ExecutionProvider.Cpu, null, fallback)
            : throw new InvalidOperationException($"ONNX Runtime cannot run on the CPU: {cpuError}");
    }

    // Auto's TensorRT-RTX selection, when it is registered on DirectML's GPU and its probe runs.
    private static ProviderSelection? Accelerated(IReadOnlyList<WindowsMLDevice> accelerators, IReadOnlyList<GpuAdapter> adapters, GpuAdapter best, IProviderProbe probe)
    {
        WindowsMLDevice? device = accelerators.FirstOrDefault(d => d.EpName == AutoAccelerator && d.AdapterIn(adapters) == best);
        if (device is null)
        {
            return null;
        }

        ProviderSelection candidate = new(ExecutionProvider.WindowsML, best, null, device.EpName)
        {
            AcceleratedModels = AutoAcceleratedModels,
            EnginesReadyOnly = true,
        };
        return probe.TryRun(candidate, out _) ? candidate : null;
    }

    /// <summary>Reads a --provider value: auto, dml (or directml), winml or cpu.</summary>
    /// <param name="value">The flag's value.</param>
    /// <param name="preference">The preference it names.</param>
    /// <returns>Whether the value was one of those.</returns>
    public static bool TryParse(string? value, out ProviderPreference preference)
    {
        preference = value?.Trim().ToUpperInvariant() switch
        {
            "AUTO" => ProviderPreference.Auto,
            "DML" or "DIRECTML" or "GPU" => ProviderPreference.DirectML,
            "WINML" or "WINDOWSML" => ProviderPreference.WindowsML,
            "CPU" => ProviderPreference.Cpu,
            _ => (ProviderPreference)(-1),
        };
        return Enum.IsDefined(preference);
    }
}

/// <summary>Builds the SessionOptions every model session uses for a selection.</summary>
public static class SessionOptionsFactory
{
    /// <summary>Options for the selected provider; the caller disposes them.</summary>
    /// <param name="selection">The provider, its GPU and its accelerator.</param>
    /// <returns>The options.</returns>
    public static SessionOptions Create(ProviderSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        SessionOptions options = new();
        try
        {
            switch (selection.Provider)
            {
                case ExecutionProvider.DirectML:
                    // DirectML runs neither with memory patterns nor with parallel execution.
                    options.EnableMemoryPattern = false;
                    options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                    options.AppendExecutionProvider_DML(selection.Adapter?.Index ?? 0);
                    break;
                case ExecutionProvider.WindowsML:
                    WindowsMLProviders.Append(options, selection);
                    break;
            }

            return options;
        }
        catch
        {
            options.Dispose();
            throw;
        }
    }
}

/// <summary>Tries a provider by running the probe model through a real ONNX Runtime session.</summary>
public sealed class OnnxRuntimeProbe : IProviderProbe
{
    private static readonly byte[] Model = ProbeModel.Build();

    /// <inheritdoc/>
    public bool TryRun(ProviderSelection candidate, out string? error)
    {
        // The append is inside the try on purpose: the PB5 spike saw it throw 887A0004 on a GPU
        // that worked before and after, before any session existed.
        try
        {
            using SessionOptions options = SessionOptionsFactory.Create(candidate);
            using InferenceSession session = new(Model, options);
            DenseTensor<float> input = new([1, 3, 16, 16]);
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
                session.Run([NamedOnnxValue.CreateFromTensor(session.InputNames[0], input)]);
            error = null;
            return results.Count == 1;
        }
        catch (Exception failure) when (failure is OnnxRuntimeException or DllNotFoundException or InvalidOperationException)
        {
            error = failure.Message;
            return false;
        }
    }
}
