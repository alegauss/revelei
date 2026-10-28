using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Revelei.Spike.Diffusion;
using SkiaSharp;

// PB77: generates one image through stable-diffusion.cpp's C API and prints what the spike
// records: load time, time per step and per image, peak video memory (sampled from nvidia-smi)
// and peak RAM, whether the progress callback arrives and whether a cancel stops the run.
//
//   DiffusionSpike <dll folder> <diffusion model> <llm> <vae> <out.png> [--size 1024x1024]
//                  [--steps 8] [--cfg 1] [--offload] [--cancel-at <step>] [--seed 42] [--prompt "..."]

if (args.Length < 5)
{
    Console.Error.WriteLine("usage: DiffusionSpike <dll folder> <diffusion model> <llm> <vae> <out.png> [--size WxH] [--steps N] [--cfg F] [--offload] [--cancel-at N] [--seed N] [--prompt TEXT]");
    return 2;
}

string dlls = args[0];
string Option(string name, string fallback)
{
    int at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : fallback;
}

string[] size = Option("--size", "1024x1024").Split('x');
int width = int.Parse(size[0], CultureInfo.InvariantCulture), height = int.Parse(size[1], CultureInfo.InvariantCulture);
int steps = int.Parse(Option("--steps", "8"), CultureInfo.InvariantCulture);
float cfg = float.Parse(Option("--cfg", "1"), CultureInfo.InvariantCulture);
int cancelAt = int.Parse(Option("--cancel-at", "-1"), CultureInfo.InvariantCulture);
long seed = long.Parse(Option("--seed", "42"), CultureInfo.InvariantCulture);
bool offload = args.Contains("--offload");
string prompt = Option("--prompt", "A red fox sitting in fresh snow at sunrise, detailed fur, soft golden light, photograph");

Native.SetDllDirectory(dlls);
NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, _, _) =>
    name == "stable-diffusion.dll" ? NativeLibrary.Load(Path.Combine(dlls, name)) : 0);

List<string> errors = [];
LogCallback log = (level, text, _) =>
{
    string line = Marshal.PtrToStringUTF8(text)?.TrimEnd() ?? string.Empty;
    if (level >= 3)
    {
        errors.Add(line);
    }

    if (Environment.GetEnvironmentVariable("SPIKE_VERBOSE") == "1" || level >= 2)
    {
        Console.Error.WriteLine($"[sd {level}] {line}");
    }
};
Native.sd_set_log_callback(log, 0);
Console.WriteLine($"system: {Marshal.PtrToStringUTF8(Native.sd_get_system_info())?.Trim()}");

// Peak video memory: nvidia-smi sampled every 250 ms for the whole run.
long peakVram = 0, baseVram = Vram();
using CancellationTokenSource stopSampling = new();
Task sampler = Task.Run(async () =>
{
    while (!stopSampling.IsCancellationRequested)
    {
        peakVram = Math.Max(peakVram, Vram());
        await Task.Delay(250).ConfigureAwait(false);
    }
});

List<nint> strings = [];
nint Utf8(string value)
{
    nint p = Marshal.StringToCoTaskMemUTF8(value);
    strings.Add(p);
    return p;
}

SdCtxParams ctxParams = default;
Native.sd_ctx_params_init(ref ctxParams);
ctxParams.DiffusionModelPath = Utf8(args[1]);
ctxParams.LlmPath = Utf8(args[2]);
ctxParams.VaePath = Utf8(args[3]);
if (Option("--flash", string.Empty) is { Length: > 0 } flash)
{
    ctxParams.FlashAttnMode = Utf8(flash);
}

if (offload)
{
    // --offload-to-cpu in sd-cli sets the params backend to the CPU (examples/common).
    ctxParams.ParamsBackend = Utf8("cpu");
}

Console.WriteLine(Marshal.PtrToStringUTF8(Native.sd_ctx_params_to_str(ref ctxParams)));

Stopwatch clock = Stopwatch.StartNew();
nint ctx = Native.new_sd_ctx(ref ctxParams);
if (ctx == 0)
{
    Console.WriteLine($"new_sd_ctx failed: {string.Join(" | ", errors)}");
    return 1;
}

double loadSeconds = clock.Elapsed.TotalSeconds;
List<(int Step, double At)> stepTimes = [];
clock.Restart();
ProgressCallback progress = (step, total, time, _) =>
{
    stepTimes.Add((step, clock.Elapsed.TotalSeconds));
    Console.WriteLine($"  step {step}/{total} {time:0.00} s/it");
    if (step == cancelAt)
    {
        Native.sd_cancel_generation(ctx, 0);
    }
};
Native.sd_set_progress_callback(progress, 0);

SdImgGenParams genParams = default;
Native.sd_img_gen_params_init(ref genParams);
genParams.Prompt = Utf8(prompt);
genParams.NegativePrompt = Utf8(string.Empty);
genParams.Width = width;
genParams.Height = height;
genParams.Seed = seed;
genParams.BatchCount = 1;
genParams.SampleParams.SampleSteps = steps;
genParams.SampleParams.Guidance.TxtCfg = cfg;
Console.WriteLine(Marshal.PtrToStringUTF8(Native.sd_img_gen_params_to_str(ref genParams)));

clock.Restart();
bool ok = Native.generate_image(ctx, ref genParams, out nint result);
double genSeconds = clock.Elapsed.TotalSeconds;
await stopSampling.CancelAsync().ConfigureAwait(false);
await sampler.ConfigureAwait(false);

Process self = Process.GetCurrentProcess();
Console.WriteLine($"load {loadSeconds:0.0} s; generate {genSeconds:0.0} s; ok {ok}; steps seen {stepTimes.Count}; " +
    $"peak vram {(peakVram - baseVram) / 1024.0:0.00} GB over a {baseVram / 1024.0:0.00} GB baseline; peak ram {self.PeakWorkingSet64 / (double)(1L << 30):0.00} GB");

if (ok && result != 0)
{
    SdImageGenerationResult generated = Marshal.PtrToStructure<SdImageGenerationResult>(result);
    if (generated.Count > 0)
    {
        SdImageResult first = Marshal.PtrToStructure<SdImageResult>(generated.Items);
        Save(first.Image, args[4]);
        Console.WriteLine($"wrote {args[4]} ({first.Image.Width}x{first.Image.Height})");
    }

    Native.free_sd_image_generation_result(result);
}
else
{
    Console.WriteLine($"no image: {string.Join(" | ", errors)}");
}

Native.free_sd_ctx(ctx);
strings.ForEach(Marshal.FreeCoTaskMem);
GC.KeepAlive(log);
GC.KeepAlive(progress);
return ok ? 0 : 3;

static long Vram()
{
    try
    {
        using Process smi = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.used --format=csv,noheader,nounits -i 0")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        string text = smi.StandardOutput.ReadToEnd();
        smi.WaitForExit();
        return long.TryParse(text.Trim(), CultureInfo.InvariantCulture, out long mib) ? mib : 0;
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return 0;
    }
}

static unsafe void Save(SdImage image, string path)
{
    using SKBitmap bitmap = new((int)image.Width, (int)image.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
    byte* source = (byte*)image.Data;
    for (int y = 0; y < image.Height; y++)
    {
        for (int x = 0; x < image.Width; x++)
        {
            byte* p = source + (((y * image.Width) + x) * image.Channel);
            bitmap.SetPixel(x, y, new SKColor(p[0], p[1], p[2]));
        }
    }

    using FileStream file = File.Create(path);
    bitmap.Encode(file, SKEncodedImageFormat.Png, 100);
}
