using System.Diagnostics;
using System.Globalization;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Revelei.Spike.OnnxRuntime;

// PB5 spike: does this ONNX Runtime package load and run in a self-contained publish, and what
// does each provider cost? Usage: OrtSpike <cpu|dml|cuda> [size] [device] [--placement] [--model <path>]
// Prints one key=value line per measurement; --placement turns on ORT's verbose log, whose node
// placement lines say which ops fell back to CPU. --model runs an exported model instead of the
// built-in one (PB9 used it to measure INT8 on DirectML); its first input must be 1x3xHxW float.

string provider = args.Length > 0 ? args[0] : "cpu";
int size = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 256;
int device = args.Length > 2 && !args[2].StartsWith('-') ? int.Parse(args[2], CultureInfo.InvariantCulture) : 0;
bool placement = args.Contains("--placement");
int modelFlag = Array.IndexOf(args, "--model");
string? modelPath = modelFlag >= 0 && modelFlag + 1 < args.Length ? args[modelFlag + 1] : null;

Stopwatch process = Stopwatch.StartNew();
if (placement)
{
    EnvironmentCreationOptions verbose = new() { logLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_VERBOSE };
    OrtEnv.CreateInstanceWithOptions(ref verbose);
}

#if WINML
// Windows ML lists the execution providers certified for this machine's hardware and fetches the
// ones not yet present through Windows Update; registering them makes them ORT EP devices.
Stopwatch ensure = Stopwatch.StartNew();
Microsoft.Windows.AI.MachineLearning.ExecutionProviderCatalog catalog =
    Microsoft.Windows.AI.MachineLearning.ExecutionProviderCatalog.GetDefault();
foreach (Microsoft.Windows.AI.MachineLearning.ExecutionProvider found in catalog.FindAllProviders())
{
    Report("catalog_provider", $"{found.Name} {found.ReadyState}");
}

await catalog.EnsureAndRegisterCertifiedAsync();
Report("ensure_and_register_ms", ensure.Elapsed.TotalMilliseconds);
foreach (OrtEpDevice ep in OrtEnv.Instance().GetEpDevices())
{
    Report("ep_device", $"{ep.EpName} {ep.HardwareDevice.Type} vendor={ep.EpVendor}");
}
#endif

byte[] model = modelPath is null ? TinyModel.Build() : File.ReadAllBytes(modelPath);
Report("model_bytes", model.Length);
Report("ort_version", OrtEnv.Instance().GetVersionString());
Report("available_providers", string.Join(",", OrtEnv.Instance().GetAvailableProviders()));

DenseTensor<float> input = new([1, 3, size, size]);
for (int i = 0; i < input.Length; i++)
{
    input.SetValue(i, (i % 251) / 250f);
}

float[] reference = Run("cpu", out _, out _, out _);
float[] actual = Run(provider, out double create, out double first, out double median);

float maxDiff = 0;
for (int i = 0; i < reference.Length; i++)
{
    maxDiff = Math.Max(maxDiff, Math.Abs(reference[i] - actual[i]));
}

Report("provider", provider);
Report("input", $"1x3x{size}x{size}");
Report("session_create_ms", create);
Report("first_run_ms", first);
Report("median_run_ms", median);
Report("max_abs_diff_vs_cpu", maxDiff);
Report("process_ms", process.Elapsed.TotalMilliseconds);
return 0;

float[] Run(string name, out double createMs, out double firstMs, out double medianMs)
{
    using SessionOptions options = new();
    options.LogSeverityLevel = placement ? OrtLoggingLevel.ORT_LOGGING_LEVEL_VERBOSE : OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING;
    switch (name)
    {
        case "cpu":
            break;
        case "dml":
            // DirectML requires both: it cannot run with memory patterns or parallel execution.
            options.EnableMemoryPattern = false;
            options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            options.AppendExecutionProvider_DML(device);
            break;
        case "cuda":
            options.AppendExecutionProvider_CUDA(device);
            break;
#if WINML
        case "winml-gpu":
            // Windows ML's own choice among the registered providers, asked to prefer a GPU.
            options.SetEpSelectionPolicy(ExecutionProviderDevicePolicy.PREFER_GPU);
            break;
        case "winml-npu":
            options.SetEpSelectionPolicy(ExecutionProviderDevicePolicy.PREFER_NPU);
            break;
#endif
        default:
            throw new ArgumentException($"unknown provider {name}");
    }

    Stopwatch watch = Stopwatch.StartNew();
    using InferenceSession session = new(model, options);
    createMs = watch.Elapsed.TotalMilliseconds;

    NamedOnnxValue[] feeds = [NamedOnnxValue.CreateFromTensor(session.InputNames[0], input)];
    double[] runs = new double[6];
    float[] output = [];
    for (int i = 0; i < runs.Length; i++)
    {
        watch.Restart();
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = session.Run(feeds);
        runs[i] = watch.Elapsed.TotalMilliseconds;
        output = results[0].AsEnumerable<float>().ToArray();
    }

    firstMs = runs[0];
    double[] steady = runs[1..];
    Array.Sort(steady);
    medianMs = steady[steady.Length / 2];
    return output;
}

static void Report(string key, object value) =>
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{key}={value}"));
