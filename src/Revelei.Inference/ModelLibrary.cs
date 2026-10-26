using System.Collections.Concurrent;
using Microsoft.ML.OnnxRuntime;

namespace Revelei.Inference;

/// <summary>A model on disk, as the About panel lists it (PB46).</summary>
/// <param name="Name">Its folder name.</param>
/// <param name="Meta">Its meta.json.</param>
/// <param name="Variants">The variants whose ONNX files are present, e.g. fp32 and int8.</param>
public sealed record InstalledModel(string Name, ModelMeta Meta, IReadOnlyList<string> Variants);

/// <summary>
/// The models folders as the pipelines use them (PB19): &lt;root&gt;/&lt;model&gt;/meta.json beside the
/// ONNX variants, and one session cache on the detected provider. Several roots are searched in
/// order (PB51): the set the installer put beside the app, then the folder downloads go to, and a
/// model's variant is read from the first root that has it.
/// </summary>
public sealed class ModelLibrary : IDisposable
{
    private readonly ConcurrentDictionary<string, ModelMeta> metas = new(StringComparer.Ordinal);
    private readonly Lazy<SessionCache<InferenceSession>>? cpu;

    /// <summary>Opens one models folder.</summary>
    /// <param name="root">The folder holding one subfolder per model.</param>
    /// <param name="provider">Where sessions run.</param>
    /// <param name="idle">How long an unused session is kept.</param>
    public ModelLibrary(string root, ProviderSelection provider, TimeSpan? idle = null)
        : this([root], provider, idle)
    {
    }

    /// <summary>Opens several models folders, searched in order.</summary>
    /// <param name="roots">The folders, each holding one subfolder per model; the first that has a file wins.</param>
    /// <param name="provider">Where sessions run.</param>
    /// <param name="idle">How long an unused session is kept.</param>
    /// <param name="engines">
    /// PB52: where a Windows ML provider's compiled engines are kept; null uses
    /// <see cref="EngineCache.DefaultFolder"/>. Other providers build every session from the model.
    /// </param>
    public ModelLibrary(IReadOnlyList<string> roots, ProviderSelection provider, TimeSpan? idle = null, EngineCache? engines = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentOutOfRangeException.ThrowIfZero(roots.Count);
        Roots = roots;
        Provider = provider;
        TimeSpan keep = idle ?? SessionCache<InferenceSession>.DefaultIdle;
        if (provider.Provider == ExecutionProvider.WindowsML)
        {
            engines ??= new EngineCache(EngineCache.DefaultFolder);
        }

        Engines = provider.Provider == ExecutionProvider.WindowsML ? engines : null;
        Sessions = ModelSessions.Create(PathOf, provider, keep, Engines);
        if (provider.Provider != ExecutionProvider.Cpu)
        {
            cpu = new(() => ModelSessions.Create(PathOf, new ProviderSelection(ExecutionProvider.Cpu, null, null), keep));
        }
    }

    /// <summary>Where <see cref="Sessions"/> run.</summary>
    public ProviderSelection Provider { get; }

    /// <summary>CPU sessions for a GPU run that ran out of memory (PB21); null when already on CPU.</summary>
    public SessionCache<InferenceSession>? CpuFallback => cpu?.Value;

    /// <summary>The models folders, in the order they are searched.</summary>
    public IReadOnlyList<string> Roots { get; }

    /// <summary>The sessions, loaded on demand.</summary>
    public SessionCache<InferenceSession> Sessions { get; }

    /// <summary>A Windows ML provider's compiled engines (PB52); null on DirectML and CPU.</summary>
    public EngineCache? Engines { get; }

    /// <summary>The ONNX file a model's variant is read from: in the first root that has it.</summary>
    /// <param name="model">The model's folder name.</param>
    /// <param name="variant">fp32, fp16 or int8.</param>
    /// <returns>The path, in the first root when none has it.</returns>
    public string FileOf(string model, string variant) => PathOf(new ModelKey(model, variant));

    /// <summary>
    /// Where a model's variant runs, which the log names (PB52): <see cref="Provider"/>, or
    /// DirectML on its GPU for a model the Windows ML provider does not run or has no engine for.
    /// </summary>
    /// <param name="model">The model's folder name.</param>
    /// <param name="variant">fp32, fp16 or int8.</param>
    /// <returns>The selection its session is built on.</returns>
    public ProviderSelection RunsOn(string model, string variant) => ModelSessions.RunsOn(Provider, Engines, model, FileOf(model, variant));

    /// <summary>Whether a model's variant is on disk.</summary>
    /// <param name="model">The model's folder name.</param>
    /// <param name="variant">fp32, fp16 or int8.</param>
    /// <returns>True when one root holds both its meta.json and its ONNX file.</returns>
    public bool Has(string model, string variant) => RootOf(model, variant) is not null;

    /// <summary>A model's meta.json, read once, from the first root that has one.</summary>
    /// <param name="model">The model's folder name.</param>
    /// <returns>Its description.</returns>
    public ModelMeta Meta(string model) => metas.GetOrAdd(model, name =>
    {
        string root = Roots.FirstOrDefault(r => File.Exists(Path.Combine(r, name, "meta.json"))) ?? Roots[0];
        return ModelMeta.Load(Path.Combine(root, name, "meta.json"));
    });

    /// <summary>
    /// Every model in any root with a meta.json this build can read, by name, with the variants
    /// on disk across the roots (PB46: what the About panel lists, licenses included). A folder
    /// whose meta.json is unreadable is left out rather than failing the list.
    /// </summary>
    /// <returns>The models, by folder name.</returns>
    public IReadOnlyList<InstalledModel> Installed()
    {
        string[] names = [.. Roots.Where(Directory.Exists)
            .SelectMany(Directory.EnumerateDirectories)
            .Where(folder => File.Exists(Path.Combine(folder, "meta.json")))
            .Select(folder => Path.GetFileName(folder))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

        List<InstalledModel> found = [];
        foreach (string name in names)
        {
            try
            {
                ModelMeta meta = Meta(name);
                string[] onDisk = [.. meta.Variants.Keys.Where(variant => Has(name, variant)).Order(StringComparer.Ordinal)];
                found.Add(new(name, meta, onDisk));
            }
            catch (InvalidDataException)
            {
                // Listed nowhere: the operation that needs it says why when asked.
            }
        }

        return found;
    }

    /// <summary>Releases every session.</summary>
    public void Dispose()
    {
        Sessions.Dispose();
        if (cpu is { IsValueCreated: true })
        {
            cpu.Value.Dispose();
        }
    }

    private string? RootOf(string model, string variant) => Roots.FirstOrDefault(root =>
        File.Exists(Path.Combine(root, model, "meta.json")) && File.Exists(Path.Combine(root, model, variant + ".onnx")));

    private string PathOf(ModelKey key) =>
        Path.Combine(RootOf(key.Model, key.Variant) ?? Roots[0], key.Model, key.Variant + ".onnx");
}
