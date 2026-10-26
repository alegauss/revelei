using Microsoft.ML.OnnxRuntime;
using Revelei.Imaging;

namespace Revelei.Inference.Pipelines;

/// <summary>
/// The model a matting run calls: a 1 x 4 x H x W tensor in (planar RGB, then the trimap), the
/// H x W alpha out.
/// </summary>
/// <param name="input">Planar RGB in the model's range, then the trimap plane.</param>
/// <param name="width">W.</param>
/// <param name="height">H.</param>
/// <returns>The alpha, 0 to 1.</returns>
public delegate float[] MattingModel(float[] input, int width, int height);

/// <summary>
/// Edge matting with ViTMatte (PB48): where RMBG-1.4's mask is unsure, a matting model reads the
/// photo and a trimap made from that mask, and its alpha replaces the mask there. RMBG's confident
/// 0 and 1 are kept. The model runs in tiles that skip everything far from the edge: its four
/// global-attention blocks grow with the token count squared, so a 12 MP photo cannot run whole.
/// </summary>
/// <remarks>
/// Measured on example_input.jpg (933 x 1405, CPU, fp32), the semi-transparent band (alpha 10%
/// to 90%) against RMBG's own 8699 pixels: 512 px tiles with 64 px of context widened it to
/// 10174, since ViTMatte reads the edge from far around it; 1024 px with 128 px of context gave
/// 7958 in 4.4 s, and one 1536 px tile 7562, at about four times the attention memory. A trimap
/// floor of 0.1 beat 0.02 at every size (8289 against 7562 in one tile).
/// export-and-quantize.py's trimap_from_mask is the reference for <see cref="Trimap"/>.
/// </remarks>
public static class Matting
{
    /// <summary>The model's folder name.</summary>
    public const string Model = "vitmatte-small-composition-1k";

    /// <summary>The trimap's value for unknown, between background (0) and foreground (1).</summary>
    public const float Unknown = 0.5f;

    /// <summary>Mask values within this of 0 or 1 are confident: the trimap keeps them as they are.</summary>
    public const float TrimapFloor = 0.1f;

    /// <summary>The tile core side; 1280 px with its context, about 1 GB per global-attention block.</summary>
    public const int TileSide = 1024;

    /// <summary>Context read around each tile, so the model sees past the core's edge.</summary>
    public const int Context = 128;

    /// <summary>
    /// How far the unknown region grows past the unsure band: 8 px on the 1405 px test photo,
    /// where 16 widened the band again, scaled with the photo's long side.
    /// </summary>
    /// <param name="width">The photo's columns.</param>
    /// <param name="height">The photo's rows.</param>
    /// <returns>The radius in pixels, at least 4.</returns>
    public static int GrowRadius(int width, int height) => Math.Max(4, (int)Math.Ceiling(8.0 * Math.Max(width, height) / 1405));

    /// <summary>Runs the real model on the mask's unsure band; call it from the inference queue.</summary>
    /// <param name="library">The models and their sessions.</param>
    /// <param name="image">The photo.</param>
    /// <param name="mask">RMBG's stretched mask at the photo's size, 0 to 1; refined in place.</param>
    /// <param name="variant">fp32 or int8.</param>
    /// <param name="floor">Mask values within this of 0 or 1 are confident and kept.</param>
    /// <param name="tileSide">The tile core side.</param>
    /// <param name="context">Context read around each tile.</param>
    /// <param name="progress">Called with (done, total) before the first tile and after each; may throw to cancel.</param>
    public static void Run(ModelLibrary library, RgbaImage image, float[] mask, string variant, float floor = TrimapFloor, int tileSide = TileSide, int context = Context, Action<int, int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        ModelMeta meta = library.Meta(Model);
        if (meta.Input.Channels != 4 || meta.Output.Channels != 1)
        {
            throw new InvalidDataException($"{Model}'s meta.json does not describe a matting model.");
        }

        using SessionLease<InferenceSession> lease = library.Sessions.Acquire(new ModelKey(Model, variant));
        Refine(image, mask, meta, (input, width, height) => RunModel(lease.Session, meta, input, width, height), floor, tileSide, context, progress);
    }

    /// <summary>
    /// The model-agnostic half, so it can be tested with a stand-in: trimap, tiles, and the
    /// model's alpha written back where the trimap is unknown.
    /// </summary>
    /// <param name="image">The photo.</param>
    /// <param name="mask">The mask at the photo's size, 0 to 1; refined in place.</param>
    /// <param name="meta">The model's description, for its input range and multiple.</param>
    /// <param name="model">The model.</param>
    /// <param name="floor">Mask values within this of 0 or 1 are confident and kept.</param>
    /// <param name="tileSide">The tile core side.</param>
    /// <param name="context">Context read around each tile.</param>
    /// <param name="progress">
    /// PB62: called with (done, total) before the first tile and after each, the total being the
    /// tiles that hold any of the edge, so an 8192 px photo's minute of matting shows as tiles;
    /// may throw to cancel.
    /// </param>
    /// <returns>How many tiles the model ran.</returns>
    public static int Refine(RgbaImage image, float[] mask, ModelMeta meta, MattingModel model, float floor = TrimapFloor, int tileSide = TileSide, int context = Context, Action<int, int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(model);
        float[] trimap = Trimap(mask, image.Width, image.Height, GrowRadius(image.Width, image.Height), floor);

        int multiple = Math.Max(1, meta.Input.Multiple);
        RgbaImage padded = Tiler.PadToMultiple(image, multiple);
        float low = (float)meta.Input.ValueRange[0];
        float high = (float)meta.Input.ValueRange[1];
        int ran = 0;
        Tile[] edge = [.. Tiler.Plan(padded.Width, padded.Height, tileSide, context, multiple).Where(tile => AnyUnknown(trimap, image.Width, image.Height, tile.Core))];
        progress?.Invoke(0, edge.Length);
        foreach (Tile tile in edge)
        {
            PixelRect input = tile.Input;
            float[] tensor = new float[4 * input.Area];
            TensorConversion.ToTensor(padded, input, low, high, tensor.AsSpan(0, 3 * input.Area));
            Span<float> plane = tensor.AsSpan(3 * input.Area);
            for (int y = 0; y < input.Height; y++)
            {
                // The padding repeats the last row and column, as PadToMultiple does for the photo.
                int sy = Math.Min(input.Y + y, image.Height - 1);
                for (int x = 0; x < input.Width; x++)
                {
                    int sx = Math.Min(input.X + x, image.Width - 1);
                    plane[(y * input.Width) + x] = trimap[(sy * image.Width) + sx];
                }
            }

            float[] alpha = model(tensor, input.Width, input.Height);
            if (alpha.Length != input.Area)
            {
                throw new InvalidOperationException($"{Model} returned {alpha.Length} values for a {input.Width} x {input.Height} tile.");
            }

            int right = Math.Min(tile.Core.X + tile.Core.Width, image.Width);
            int bottom = Math.Min(tile.Core.Y + tile.Core.Height, image.Height);
            for (int y = tile.Core.Y; y < bottom; y++)
            {
                for (int x = tile.Core.X; x < right; x++)
                {
                    int i = (y * image.Width) + x;
                    if (trimap[i] == Unknown)
                    {
                        mask[i] = Math.Clamp(alpha[((y - input.Y) * input.Width) + (x - input.X)], 0f, 1f);
                    }
                }
            }

            ran++;
            progress?.Invoke(ran, edge.Length);
        }

        return ran;
    }

    /// <summary>
    /// 0 background, <see cref="Unknown"/> and 1 foreground: unknown where the mask is between the
    /// floor and 1 minus it, grown by the radius, the rest the mask thresholded at a half.
    /// </summary>
    /// <param name="mask">The mask, 0 to 1, row by row.</param>
    /// <param name="width">Columns.</param>
    /// <param name="height">Rows.</param>
    /// <param name="radius">How far the unsure band grows, as a (2 radius + 1) square.</param>
    /// <param name="floor">Values within this of 0 or 1 are confident.</param>
    /// <returns>The trimap, row by row.</returns>
    public static float[] Trimap(float[] mask, int width, int height, int radius, float floor)
    {
        ArgumentNullException.ThrowIfNull(mask);
        bool[] unsure = new bool[mask.Length];
        for (int i = 0; i < mask.Length; i++)
        {
            unsure[i] = mask[i] > floor && mask[i] < 1 - floor;
        }

        bool[] grown = Grow(Grow(unsure, width, height, radius, horizontal: true), width, height, radius, horizontal: false);
        float[] trimap = new float[mask.Length];
        for (int i = 0; i < mask.Length; i++)
        {
            trimap[i] = grown[i] ? Unknown : mask[i] >= 0.5f ? 1f : 0f;
        }

        return trimap;
    }

    // A dilation along one axis, by a running count of set cells in the window.
    private static bool[] Grow(bool[] set, int width, int height, int radius, bool horizontal)
    {
        bool[] result = new bool[set.Length];
        int lines = horizontal ? height : width;
        int length = horizontal ? width : height;
        for (int line = 0; line < lines; line++)
        {
            int At(int k) => horizontal ? (line * width) + k : (k * width) + line;
            int count = 0;
            for (int k = 0; k < Math.Min(radius, length); k++)
            {
                count += set[At(k)] ? 1 : 0;
            }

            for (int k = 0; k < length; k++)
            {
                if (k + radius < length && set[At(k + radius)])
                {
                    count++;
                }

                if (k - radius - 1 >= 0 && set[At(k - radius - 1)])
                {
                    count--;
                }

                result[At(k)] = count > 0;
            }
        }

        return result;
    }

    private static bool AnyUnknown(float[] trimap, int width, int height, PixelRect core)
    {
        int right = Math.Min(core.X + core.Width, width);
        int bottom = Math.Min(core.Y + core.Height, height);
        for (int y = core.Y; y < bottom; y++)
        {
            for (int x = core.X; x < right; x++)
            {
                if (trimap[(y * width) + x] == Unknown)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static float[] RunModel(InferenceSession session, ModelMeta meta, float[] input, int width, int height)
    {
        using OrtValue tensor = OrtValue.CreateTensorValueFromMemory(input, [1, 4, height, width]);
        using RunOptions run = new();
        using IDisposableReadOnlyCollection<OrtValue> outputs = session.Run(run, [meta.Input.Name], [tensor], [meta.Output.Name]);
        return outputs[0].GetTensorDataAsSpan<float>().ToArray();
    }
}
