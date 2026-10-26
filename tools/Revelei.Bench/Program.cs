using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Win32;
using Revelei.Bench;
using Revelei.Imaging;
using Revelei.Inference;
using Revelei.Inference.Pipelines;

// PB34: every operation, on its model's own test photo, in FP16 and FP32, on the CPU and on
// DirectML when this desk has it, printed as the Markdown table the README carries.
//
//   dotnet run --project tools\Revelei.Bench -c Release -- [--models <dir>] [--images <dir>] [--out <file.md>]

string root = Repository();
string models = Arg("--models") ?? Path.Combine(root, ".cache", "models");
string images = Arg("--images") ?? Path.Combine(root, ".cache", "weights", "test-images");
string? output = Arg("--out");

List<ProviderSelection> providers = [new(ExecutionProvider.Cpu, null, null)];
ProviderSelection gpu = ProviderDetector.Detect(ProviderPreference.DirectML);
if (gpu.Provider == ExecutionProvider.DirectML)
{
    providers.Add(gpu);
}

Operation[] operations =
[
    new("Upscale 4×", "realesrgan-x4plus", "0014.jpg", (library, image, variant) => Upscale.Run(library, image, 4, variant, tile: 256)),
    new("Upscale 2×", "realesrgan-x2plus", "0014.jpg", (library, image, variant) => Upscale.Run(library, image, 2, variant, tile: 256)),
    new("Restore faces", "gfpgan-v1.4", "Blake_Lively.jpg", (library, image, variant) => FaceRestore.Run(library, image, variant).Image),
    new("Remove background", "rmbg-1.4", "example_input.jpg", (library, image, variant) => RemoveBackground.Run(library, image, variant, refine: false), AlphaOnly: true),
    new("Remove noise", "scunet-color-real-psnr", "building.png", (library, image, variant) => Denoise.Run(library, image, variant, tile: 256)),
];

string[] variants = ["fp16", "fp32"];
StringBuilder table = new();
table.AppendLine(CultureInfo.InvariantCulture, $"Measured on {DateTime.Now:yyyy-MM-dd}: {Cpu()}; {(gpu.Adapter is { } adapter ? adapter.Label : "no DirectML GPU")}.");
table.AppendLine();
table.AppendLine("| Operation | Provider | Variant | File | First run | Run | Peak memory | PSNR vs FP32 | SSIM vs FP32 |");
table.AppendLine("| --- | --- | --- | --: | --: | --: | --: | --: | --: |");

foreach (Operation operation in operations)
{
    string path = Path.Combine(images, operation.Image);
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"{operation.Image} is missing from {images}; run scripts\\download_models.py.");
        return 2;
    }

    RgbaImage photo = ImageDecoder.Decode(path);
    foreach (ProviderSelection provider in providers)
    {
        Dictionary<string, (Measurement Measured, RgbaImage Result)> runs = [];
        foreach (string variant in variants)
        {
            GC.Collect();
            long before = MemoryUse.Now(provider);
            using MemoryUse.Sampler sampler = new(provider);
            using ModelLibrary library = new(models, provider);
            if (!library.Has(operation.Model, variant))
            {
                Console.Error.WriteLine($"{operation.Model} ({variant}) is not in {models}; run scripts\\export-and-quantize.py.");
                return 2;
            }

            Console.Error.WriteLine($"{operation.Name} · {provider.Name} · {variant}");
            runs[variant] = Measure(() => operation.Run(library, photo, variant));
            runs[variant] = runs[variant] with { Measured = runs[variant].Measured with { PeakBytes = Math.Max(0, sampler.Peak - before) } };
            runs[variant] = runs[variant] with { Measured = runs[variant].Measured with { Bytes = library.Meta(operation.Model).Variants[variant].Bytes } };
        }

        RgbaImage reference = runs["fp32"].Result;
        foreach (string variant in variants)
        {
            (Measurement measured, RgbaImage result) = runs[variant];
            string quality = variant == "fp32"
                ? "— | —"
                : string.Create(CultureInfo.InvariantCulture, $"{Quality.Psnr(result, reference, operation.AlphaOnly):0.0} dB | {Quality.Ssim(result, reference, operation.AlphaOnly):0.0000}");
            table.AppendLine(CultureInfo.InvariantCulture,
                $"| {operation.Name} | {(provider.Provider == ExecutionProvider.Cpu ? "CPU" : provider.Name)} | {variant.ToUpperInvariant()} | {measured.Bytes / 1048576.0:0.0} MB | {measured.First.TotalSeconds:0.00} s | {measured.Warm.TotalSeconds:0.00} s | {measured.PeakBytes / 1048576.0:0} MB | {quality} |");
        }
    }
}

string text = table.ToString();
Console.Write(text);
if (output is not null)
{
    File.WriteAllText(output, text);
}

return 0;

// The first run loads the session; the warm run is the faster of two more. The caller samples the
// peak memory, every 10 ms from before the session loads: video memory on DirectML, private bytes on CPU.
static (Measurement, RgbaImage) Measure(Func<RgbaImage> run)
{
    Stopwatch clock = Stopwatch.StartNew();
    RgbaImage result = run();
    TimeSpan first = clock.Elapsed;

    TimeSpan warm = TimeSpan.MaxValue;
    for (int i = 0; i < 2; i++)
    {
        clock.Restart();
        result = run();
        warm = clock.Elapsed < warm ? clock.Elapsed : warm;
    }

    return (new Measurement(0, first, warm, 0), result);
}

string? Arg(string name)
{
    int at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}

static string Repository()
{
    for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
    {
        if (File.Exists(Path.Combine(folder.FullName, "Revelei.slnx")))
        {
            return folder.FullName;
        }
    }

    return Directory.GetCurrentDirectory();
}

static string Cpu() =>
    Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) is string name
        ? name.Trim()
        : "CPU";

/// <summary>One operation as the table runs it.</summary>
/// <param name="Name">Its name in the table.</param>
/// <param name="Model">The model whose file size and variants are measured.</param>
/// <param name="Image">The test photo, in the test-images folder.</param>
/// <param name="Run">Runs it on a photo in a variant.</param>
/// <param name="AlphaOnly">Compare only the alpha, for a mask.</param>
internal sealed record Operation(string Name, string Model, string Image, Func<ModelLibrary, RgbaImage, string, RgbaImage> Run, bool AlphaOnly = false);

/// <summary>What one variant measured.</summary>
/// <param name="Bytes">Its ONNX file's size.</param>
/// <param name="First">The first run, the session load included.</param>
/// <param name="Warm">The faster of two runs after it.</param>
/// <param name="PeakBytes">The sampled peak above what the process held before the session loaded: weights and activations.</param>
internal sealed record Measurement(long Bytes, TimeSpan First, TimeSpan Warm, long PeakBytes);
