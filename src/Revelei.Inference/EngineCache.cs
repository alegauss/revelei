using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Vortice.DXGI;
using WinML = Microsoft.Windows.AI.MachineLearning;

namespace Revelei.Inference;

/// <summary>
/// Compiled engines for a Windows ML provider (PB52), as ONNX Runtime EP context models: TensorRT-RTX
/// builds its engine for 10 to 17 s whenever a session is created, and loading the context model
/// it wrote takes 1.6 to 2.3 s instead (Real-ESRGAN x4 FP16 on the RTX 4060, 2026-10-20). Each
/// engine is keyed by everything that makes it stale: the model file, the provider package and its
/// version, the GPU and its driver, and the ONNX Runtime version. A key that no longer matches is a
/// new file, so a stale engine is never loaded, only left behind.
/// </summary>
public sealed class EngineCache(string folder)
{
    /// <summary>%LOCALAPPDATA%\Revelei\engines.</summary>
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Revelei", "engines");

    /// <summary>The folder the context models are written to.</summary>
    public string Folder { get; } = folder;

    /// <summary>
    /// The context model's name for one model file on one device: &lt;model&gt;-&lt;variant&gt;-&lt;key&gt;_ctx.onnx,
    /// the model being the file's folder and the variant its name.
    /// </summary>
    /// <param name="modelPath">The ONNX file.</param>
    /// <param name="key">What the engine depends on, from <see cref="Key"/>.</param>
    /// <returns>The path in <see cref="Folder"/>.</returns>
    public string PathFor(string modelPath, string key)
    {
        ArgumentNullException.ThrowIfNull(modelPath);
        string model = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(modelPath))) ?? "model";
        string variant = Path.GetFileNameWithoutExtension(modelPath);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Path.Combine(Folder, $"{model}-{variant}-{Convert.ToHexStringLower(hash)[..16]}_ctx.onnx");
    }

    /// <summary>Everything a compiled engine depends on, as one string.</summary>
    /// <param name="modelPath">The ONNX file; its size and time stand for its content.</param>
    /// <param name="provider">The provider's ONNX Runtime name.</param>
    /// <param name="package">The provider package's full name, which carries its version.</param>
    /// <param name="device">The GPU, e.g. its LUID.</param>
    /// <param name="driver">The GPU driver's version.</param>
    /// <param name="runtime">ONNX Runtime's version.</param>
    /// <returns>The key.</returns>
    public static string Key(string modelPath, string provider, string package, string device, string driver, string runtime)
    {
        FileInfo file = new(modelPath);
        return string.Join(
            '|',
            Path.GetFullPath(modelPath),
            file.Length.ToString(CultureInfo.InvariantCulture),
            file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            provider,
            package,
            device,
            driver,
            runtime);
    }

    /// <summary>
    /// A session on the selection's Windows ML provider: from the context model when this key has
    /// one, else built from the model, which also writes the context model for next time.
    /// </summary>
    /// <param name="modelPath">The ONNX file.</param>
    /// <param name="selection">A Windows ML selection.</param>
    /// <returns>The session; the caller disposes it.</returns>
    /// <exception cref="NotSupportedException">The provider could not build this model, now or on an earlier run.</exception>
    public InferenceSession Open(string modelPath, ProviderSelection selection)
    {
        string context = ContextFor(modelPath, selection);
        string unsupported = Unsupported(context);
        if (File.Exists(unsupported))
        {
            throw new NotSupportedException($"{selection.Name} could not build an engine for {modelPath} on an earlier run.");
        }

        if (File.Exists(context))
        {
            try
            {
                using SessionOptions cached = SessionOptionsFactory.Create(selection);
                return new InferenceSession(context, cached);
            }
            catch (OnnxRuntimeException failure)
            {
                // A file the provider no longer reads: build it again below, except under auto
                // (PB52), where no run builds and the warmup rebuilds it on the next start.
                File.Delete(context);
                if (selection.EnginesReadyOnly)
                {
                    throw new NotSupportedException($"{selection.Name} could not load its engine for {modelPath}; it is rebuilt on the next start.", failure);
                }
            }
        }

        Directory.CreateDirectory(Folder);
        string partial = context[..^".onnx".Length] + ".partial.onnx";
        using SessionOptions options = SessionOptionsFactory.Create(selection);
        options.AddSessionConfigEntry("ep.context_enable", "1");
        options.AddSessionConfigEntry("ep.context_file_path", partial);
        options.AddSessionConfigEntry("ep.context_embed_mode", "1");
        InferenceSession session;
        try
        {
            session = new(modelPath, options);
        }
        catch (OnnxRuntimeException failure)
        {
            // TensorRT-RTX 0.4 builds no engine for Real-ESRGAN x2plus, SCUNet or ViTMatte
            // (ShapeInferenceNotRegistered, 2026-10-20). Said once, so later runs go straight to
            // the fallback instead of failing the same build every start.
            File.Delete(partial);
            File.WriteAllText(unsupported, failure.Message);
            throw new NotSupportedException($"{selection.Name} cannot build an engine for {modelPath}: {failure.Message}", failure);
        }

        if (File.Exists(partial))
        {
            File.Move(partial, context, overwrite: true);
        }

        return session;
    }

    /// <summary>
    /// Whether this model has nothing left to build for the selection's provider and GPU: its
    /// engine is on disk, or the provider could not build one.
    /// </summary>
    /// <param name="modelPath">The ONNX file.</param>
    /// <param name="selection">A Windows ML selection.</param>
    /// <returns>True when there is nothing to build.</returns>
    public bool Has(string modelPath, ProviderSelection selection)
    {
        string context = ContextFor(modelPath, selection);
        return File.Exists(context) || File.Exists(Unsupported(context));
    }

    /// <summary>Whether this model's engine is built and on disk (PB52).</summary>
    /// <param name="modelPath">The ONNX file.</param>
    /// <param name="selection">A Windows ML selection.</param>
    /// <returns>True when its context model exists.</returns>
    public bool Ready(string modelPath, ProviderSelection selection) => File.Exists(ContextFor(modelPath, selection));

    /// <summary>Whether the provider could not build this model on an earlier run (PB55).</summary>
    /// <param name="modelPath">The ONNX file.</param>
    /// <param name="selection">A Windows ML selection.</param>
    /// <returns>True when that was recorded.</returns>
    public bool Unsupported(string modelPath, ProviderSelection selection) => File.Exists(Unsupported(ContextFor(modelPath, selection)));

    private static string Unsupported(string context) => context[..^".onnx".Length] + ".unsupported.txt";

    private string ContextFor(string modelPath, ProviderSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return PathFor(modelPath, Key(modelPath, selection.Accelerator ?? string.Empty, Package(selection), Device(selection), Driver(selection), OrtEnv.Instance().GetVersionString()));
    }

    private static string Package(ProviderSelection selection)
    {
        try
        {
            return WinML.ExecutionProviderCatalog.GetDefault().FindAllProviders()
                .FirstOrDefault(provider => provider.Name == selection.Accelerator)?.PackageId?.FullName ?? string.Empty;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            return string.Empty;
        }
    }

    private static string Device(ProviderSelection selection) =>
        selection.Adapter is { } adapter ? string.Create(CultureInfo.InvariantCulture, $"{adapter.Index}:{adapter.VendorId}:{adapter.Name}") : string.Empty;

    // The user-mode driver's version, which changes when the GPU driver does.
    private static string Driver(ProviderSelection selection)
    {
        if (selection.Adapter is not { } adapter || DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory).Failure || factory is null)
        {
            return string.Empty;
        }

        using (factory)
        {
            if (factory.EnumAdapters1((uint)adapter.Index, out IDXGIAdapter1? found).Failure || found is null)
            {
                return string.Empty;
            }

            using (found)
            {
                return found.CheckInterfaceSupport<IDXGIDevice>(out long version)
                    ? version.ToString(CultureInfo.InvariantCulture)
                    : string.Empty;
            }
        }
    }
}
