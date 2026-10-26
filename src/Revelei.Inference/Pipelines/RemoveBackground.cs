using Microsoft.ML.OnnxRuntime;
using Revelei.Imaging;

namespace Revelei.Inference.Pipelines;

/// <summary>
/// Background removal with RMBG-1.4 (PB32), as BRIA's own example does it: the photo is resized to
/// the model's square, ignoring its aspect, the mask that comes back is resized to the photo and
/// stretched so its lowest value is transparent and its highest opaque, and it becomes the alpha.
/// Colour is left alone; an alpha the photo already had is kept where it is lower. Refined, the
/// mask's unsure edge goes through ViTMatte where it is on disk (PB48), else a guided filter (PB47).
/// </summary>
public static class RemoveBackground
{
    /// <summary>The model's folder name.</summary>
    public const string Model = "rmbg-1.4";

    /// <summary>The stage RMBG-1.4's own run reports under: one step, the subject found.</summary>
    public const string Stage = "background";

    /// <summary>
    /// PB62: the stage the edge refinement reports under, ViTMatte's tiles or the guided filter's
    /// one step: on an 8192 px photo it is most of the run, and it said nothing for a minute.
    /// </summary>
    public const string EdgesStage = "edges";

    // The ViTMatte variants tried, in order, when the one asked for is not on disk.
    private static readonly string[] MatteFallbacks = ["fp32", "int8"];

    /// <summary>Removes a photo's background; call it from the inference queue.</summary>
    /// <param name="library">The models and their sessions.</param>
    /// <param name="image">The photo, left as it is.</param>
    /// <param name="variant">fp32, fp16 or int8.</param>
    /// <param name="refine">
    /// Whether to refine the mask against the photo: with ViTMatte where its model is on disk
    /// (PB48), else with the guided filter (PB47). Off is BRIA's own result.
    /// </param>
    /// <param name="progress">Called with (stage, done, total) as the subject is found and as the edges are refined; may throw to cancel.</param>
    /// <returns>The photo with the background transparent.</returns>
    public static RgbaImage Run(ModelLibrary library, RgbaImage image, string variant, bool refine = true, Action<string, int, int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(image);
        ModelMeta meta = library.Meta(Model);
        if (meta.Input.Size is not int side || meta.Output.Channels != 1)
        {
            throw new InvalidDataException($"{Model}'s meta.json does not describe a fixed-size mask model.");
        }

        string? matteVariant = refine ? MatteVariant(library, variant) : null;
        Action<float[]>? matting = matteVariant is not null
            ? mask => Matting.Run(library, image, mask, matteVariant, progress: (done, total) => progress?.Invoke(EdgesStage, done, total))
            : null;
        using SessionLease<InferenceSession> lease = library.Sessions.Acquire(new ModelKey(Model, VariantFor(library, variant)));
        progress?.Invoke(Stage, 0, 1);
        return Matte(image, side, meta, input => RunModel(lease.Session, meta, input, side), refine, matting, progress);
    }

    /// <summary>
    /// The RMBG variant a run loads (PB58): the one asked for, else FP32, which is what the first
    /// use downloads from BRIA's repository now that the installer no longer carries the FP16.
    /// </summary>
    /// <param name="library">The models.</param>
    /// <param name="variant">The variant the operation runs in.</param>
    /// <returns>The variant to load.</returns>
    public static string VariantFor(ModelLibrary library, string variant)
    {
        ArgumentNullException.ThrowIfNull(library);
        return !library.Has(Model, variant) && library.Has(Model, "fp32") ? "fp32" : variant;
    }

    /// <summary>
    /// The ViTMatte variant Refine edges runs with: the one asked for, else FP32, else INT8, since
    /// ViTMatte has no FP16 file and the bundled FP16 set brings its FP32 (PB51).
    /// </summary>
    /// <param name="library">The models.</param>
    /// <param name="variant">The variant the operation runs in.</param>
    /// <returns>The variant on disk, or null when ViTMatte is not.</returns>
    public static string? MatteVariant(ModelLibrary library, string variant)
    {
        ArgumentNullException.ThrowIfNull(library);
        return library.Has(Matting.Model, variant) ? variant : MatteFallbacks.FirstOrDefault(v => library.Has(Matting.Model, v));
    }

    /// <summary>Mask values under this become fully transparent after refining: the faint specks.</summary>
    public const float Floor = 0.02f;

    /// <summary>The guided filter's regulariser, on luminance 0 to 1: how much detail the photo needs before the edge follows it.</summary>
    public const float Epsilon = 1e-4f;

    /// <summary>
    /// The guided filter's window: about two of the model's pixels at the photo's scale, so the
    /// blur a 1024 px mask picks up from being scaled is inside the window that removes it.
    /// </summary>
    /// <param name="image">The photo.</param>
    /// <param name="side">The model's square side.</param>
    /// <returns>The radius in photo pixels, at least 2.</returns>
    public static int RefineRadius(RgbaImage image, int side)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Math.Max(2, (int)Math.Ceiling(2.0 * Math.Max(image.Width, image.Height) / side));
    }

    /// <summary>
    /// The model-agnostic half, so it can be tested with a stand-in: resize, run, and the mask
    /// brought back as alpha.
    /// </summary>
    /// <param name="image">The photo, left as it is.</param>
    /// <param name="side">The model's square input side.</param>
    /// <param name="meta">The model's description, for its value ranges.</param>
    /// <param name="model">1 x 3 x side x side in, side x side mask values out.</param>
    /// <param name="refine">
    /// PB47: where the stretched mask is unsure (between <see cref="Floor"/> and 1 minus it),
    /// replace it with a guided filter of itself on the photo's luminance, so its edge follows the
    /// full-size photo and a faint isolated speck is averaged away; then zero what is under the
    /// floor and make opaque what is within it of 1.
    /// </param>
    /// <param name="matting">
    /// PB48: with <paramref name="refine"/>, what refines the unsure band in place instead of the
    /// guided filter (ViTMatte, see <see cref="Matting"/>); null keeps the guided filter.
    /// </param>
    /// <param name="progress">
    /// PB62: called with (<see cref="Stage"/>, 1, 1) once the mask is in, and with the guided
    /// filter's one <see cref="EdgesStage"/> step around it; ViTMatte reports its own tiles.
    /// </param>
    /// <returns>A copy of the photo with the mask as its alpha.</returns>
    public static RgbaImage Matte(RgbaImage image, int side, ModelMeta meta, Func<float[], float[]> model, bool refine = false, Action<float[]>? matting = null, Action<string, int, int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(model);
        float[] input = new float[3 * side * side];
        RgbaImage small = ImageResampler.Resize(image, side, side);
        TensorConversion.ToTensor(small, small.Bounds, (float)meta.Input.ValueRange[0], (float)meta.Input.ValueRange[1], input);
        float[] mask = ImageResampler.ResizePlane(model(input), side, side, image.Width, image.Height);
        progress?.Invoke(Stage, 1, 1);

        // BRIA's postprocess: the mask stretched to its own range. A flat mask says nothing about
        // where the subject is, so it is read in the model's range instead of divided by zero.
        float low = mask.Min();
        float high = mask.Max();
        if (high - low < 1e-6f)
        {
            low = (float)meta.Output.ValueRange[0];
            high = (float)meta.Output.ValueRange[1];
        }

        for (int i = 0; i < mask.Length; i++)
        {
            mask[i] = Math.Clamp((mask[i] - low) / (high - low), 0f, 1f);
        }

        if (refine && matting is not null)
        {
            matting(mask);
            for (int i = 0; i < mask.Length; i++)
            {
                mask[i] = Snap(mask[i]);
            }
        }
        else if (refine)
        {
            // Only where BRIA's mask is unsure. Its confident 0 and 1 stay: on example_input.jpg,
            // filtering everywhere smoothed the mask out into the flat bright sky around the
            // giraffes' heads, a halo, since a flat photo gives the filter no edge to follow.
            progress?.Invoke(EdgesStage, 0, 1);
            float[] filtered = GuidedFilter.Refine(image, mask, RefineRadius(image, side), Epsilon);
            progress?.Invoke(EdgesStage, 1, 1);
            for (int i = 0; i < mask.Length; i++)
            {
                mask[i] = mask[i] < Floor || mask[i] > 1 - Floor ? Snap(mask[i]) : Snap(filtered[i]);
            }
        }

        RgbaImage result = new(image.Width, image.Height, image.SourceBitDepth);
        image.Samples.CopyTo(result.Samples);
        for (int y = 0; y < image.Height; y++)
        {
            Span<ushort> row = result.Row(y);
            for (int x = 0; x < image.Width; x++)
            {
                float t = mask[(y * image.Width) + x];
                ushort alpha = (ushort)MathF.Round(t * RgbaImage.Max);
                row[(x * 4) + 3] = Math.Min(row[(x * 4) + 3], alpha);
            }
        }

        return result;
    }

    // Within Floor of either end is that end; the guided filter can also overshoot past 0 and 1.
    private static float Snap(float value)
    {
        if (value < Floor)
        {
            return 0f;
        }

        return value > 1 - Floor ? 1f : value;
    }

    private static float[] RunModel(InferenceSession session, ModelMeta meta, float[] input, int side)
    {
        using OrtValue tensor = OrtValue.CreateTensorValueFromMemory(input, [1, 3, side, side]);
        using RunOptions run = new();
        using IDisposableReadOnlyCollection<OrtValue> outputs = session.Run(run, [meta.Input.Name], [tensor], [meta.Output.Name]);
        return outputs[0].GetTensorDataAsSpan<float>().ToArray();
    }
}
