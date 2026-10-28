using System.Runtime.InteropServices;

namespace Revelei.Generation.Host;

/// <summary>What generates: the native engine, or the stand-in the tests drive.</summary>
internal interface IEngine : IDisposable
{
    /// <summary>Generates one image into <paramref name="output"/> as RGBA8, row by row.</summary>
    /// <param name="request">What to draw.</param>
    /// <param name="output">The file to write.</param>
    /// <param name="progress">Told each stage and step.</param>
    /// <param name="preview">Given a low-resolution RGBA8 frame of the image as it forms, with its size, after a sampling step.</param>
    /// <returns>The image's size, or null when it was cancelled.</returns>
    (int Width, int Height)? Generate(GenerationRequest request, string output, Action<GenerationProgress> progress, Action<byte[], int, int> preview);

    /// <summary>Asks the running generation to stop after its current step; called from another thread.</summary>
    void Cancel();
}

/// <summary>
/// stable-diffusion.cpp through its C API (PB77). The context is kept while the next request names
/// the same files, since loading is the slow part. The engine reports loading, sampling and the
/// VAE's tiles all as steps; the sampling pass is the one whose total is the request's steps.
/// </summary>
internal sealed class NativeEngine : IEngine
{
    private readonly List<nint> strings = [];
    private readonly LogCallback log;
    private readonly List<string> errors = [];
    private ProgressCallback? callback;
    private PreviewCallback? previewing;
    private nint context;
    private (string, string, string, bool)? loaded;
    private volatile bool cancelled;

    public NativeEngine(string folder)
    {
        // ggml finds its backend DLLs (ggml-cuda, ggml-vulkan, ggml-cpu-*) in the working directory.
        Directory.SetCurrentDirectory(folder);
        Native.SetDllDirectory(folder);
        NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, _, _) =>
            name == "stable-diffusion.dll" ? NativeLibrary.Load(Path.Combine(folder, name)) : 0);
        log = (level, text, _) =>
        {
            string line = Marshal.PtrToStringUTF8(text)?.TrimEnd() ?? string.Empty;
            if (level >= 3)
            {
                lock (errors)
                {
                    errors.Add(line);
                }
            }

            if (level >= 2)
            {
                Console.Error.WriteLine(line);
            }
        };
        Native.sd_set_log_callback(log, 0);
    }

    public (int Width, int Height)? Generate(GenerationRequest request, string output, Action<GenerationProgress> progress, Action<byte[], int, int> preview)
    {
        cancelled = false;
        lock (errors)
        {
            errors.Clear();
        }

        Load(request);
        bool sampled = false;
        callback = (step, steps, _, _) =>
        {
            GenerationStage stage = steps == request.Steps && !sampled ? GenerationStage.Sampling
                : sampled ? GenerationStage.Decoding
                : GenerationStage.Loading;
            sampled |= stage == GenerationStage.Sampling && step == steps;
            if (step > 0)
            {
                progress(new GenerationProgress(stage, step, steps));
            }
        };
        Native.sd_set_progress_callback(callback, 0);
        previewing = (_, count, frames, _, _) =>
        {
            if (count > 0 && frames != 0)
            {
                SdImage frame = Marshal.PtrToStructure<SdImage>(frames);
                preview(Rgba(frame), (int)frame.Width, (int)frame.Height);
            }
        };
        Native.sd_set_preview_callback(previewing, 1, 1, true, false, 0);

        SdImgGenParams generate = default;
        Native.sd_img_gen_params_init(ref generate);
        generate.Prompt = Utf8(request.Prompt);
        generate.NegativePrompt = Utf8(request.NegativePrompt ?? string.Empty);
        generate.Width = request.Width;
        generate.Height = request.Height;
        generate.Seed = request.Seed;
        generate.BatchCount = 1;
        generate.SampleParams.SampleSteps = request.Steps;
        generate.SampleParams.Guidance.TxtCfg = request.Guidance;
        bool ok = Native.generate_image(context, ref generate, out nint result);
        if (!ok || result == 0)
        {
            if (cancelled)
            {
                return null;
            }

            lock (errors)
            {
                throw new GenerationFailedException(errors.Count > 0 ? string.Join(" ", errors.TakeLast(3)) : "The engine returned no image.");
            }
        }

        try
        {
            SdImageGenerationResult generated = Marshal.PtrToStructure<SdImageGenerationResult>(result);
            SdImage image = Marshal.PtrToStructure<SdImageResult>(generated.Items).Image;
            File.WriteAllBytes(output, Rgba(image));
            return ((int)image.Width, (int)image.Height);
        }
        finally
        {
            Native.free_sd_image_generation_result(result);
        }
    }

    public void Cancel()
    {
        cancelled = true;
        if (context != 0)
        {
            Native.sd_cancel_generation(context, 0);
        }
    }

    public void Dispose()
    {
        if (context != 0)
        {
            Native.free_sd_ctx(context);
            context = 0;
        }

        strings.ForEach(Marshal.FreeCoTaskMem);
        strings.Clear();
        GC.KeepAlive(log);
    }

    private void Load(GenerationRequest request)
    {
        (string, string, string, bool) wanted = (request.DiffusionModel, request.TextEncoder, request.Vae, request.Offload);
        if (context != 0 && loaded == wanted)
        {
            return;
        }

        if (context != 0)
        {
            Native.free_sd_ctx(context);
            context = 0;
        }

        SdCtxParams parameters = default;
        Native.sd_ctx_params_init(ref parameters);
        parameters.DiffusionModelPath = Utf8(request.DiffusionModel);
        parameters.LlmPath = Utf8(request.TextEncoder);
        parameters.VaePath = Utf8(request.Vae);
        if (request.Offload)
        {
            parameters.ParamsBackend = Utf8("cpu");
        }

        context = Native.new_sd_ctx(ref parameters);
        if (context == 0)
        {
            lock (errors)
            {
                throw new GenerationFailedException(errors.Count > 0 ? string.Join(" ", errors.TakeLast(3)) : "The engine could not load the model.");
            }
        }

        loaded = wanted;
    }

    private nint Utf8(string value)
    {
        nint pointer = Marshal.StringToCoTaskMemUTF8(value);
        strings.Add(pointer);
        return pointer;
    }

    private static unsafe byte[] Rgba(SdImage image)
    {
        int pixels = checked((int)(image.Width * image.Height));
        byte[] rgba = new byte[pixels * 4];
        byte* source = (byte*)image.Data;
        for (int i = 0; i < pixels; i++)
        {
            byte* pixel = source + (i * image.Channel);
            rgba[(i * 4) + 0] = pixel[0];
            rgba[(i * 4) + 1] = pixel[1];
            rgba[(i * 4) + 2] = pixel[2];
            rgba[(i * 4) + 3] = image.Channel == 4 ? pixel[3] : byte.MaxValue;
        }

        return rgba;
    }
}

/// <summary>
/// A stand-in with the engine's shape and none of its weight, so the process seam is tested on
/// any desk: it loads, samples and decodes in steps, honours a cancel after the current step, and
/// writes a gradient. A prompt of "crash" ends the process mid-run and "hang" ignores a cancel,
/// which is how the tests reach a native crash and a cancel that does not land.
/// </summary>
internal sealed class FakeEngine : IEngine
{
    private volatile bool cancelled;

    public (int Width, int Height)? Generate(GenerationRequest request, string output, Action<GenerationProgress> progress, Action<byte[], int, int> preview)
    {
        cancelled = false;
        progress(new GenerationProgress(GenerationStage.Loading, 1, 1));
        for (int step = 1; step <= request.Steps; step++)
        {
            Thread.Sleep(20);
            if (request.Prompt == "crash" && step == 2)
            {
                Environment.Exit(77);
            }

            if (request.Prompt == "hang" && step == 2)
            {
                Thread.Sleep(Timeout.Infinite);
            }

            progress(new GenerationProgress(GenerationStage.Sampling, step, request.Steps));
            preview([(byte)step, (byte)step, (byte)step, byte.MaxValue], 1, 1);
            if (cancelled && request.Prompt != "hang")
            {
                return null;
            }
        }

        progress(new GenerationProgress(GenerationStage.Decoding, 1, 1));
        byte[] rgba = new byte[request.Width * request.Height * 4];
        for (int y = 0; y < request.Height; y++)
        {
            for (int x = 0; x < request.Width; x++)
            {
                int at = ((y * request.Width) + x) * 4;
                rgba[at] = (byte)(x * 255 / Math.Max(1, request.Width - 1));
                rgba[at + 1] = (byte)(y * 255 / Math.Max(1, request.Height - 1));
                rgba[at + 2] = (byte)(request.Seed % 256);
                rgba[at + 3] = byte.MaxValue;
            }
        }

        File.WriteAllBytes(output, rgba);
        return (request.Width, request.Height);
    }

    public void Cancel() => cancelled = true;

    public void Dispose()
    {
    }
}
