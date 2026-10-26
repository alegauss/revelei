using Microsoft.ML.OnnxRuntime;
using Revelei.Imaging;

namespace Revelei.Inference.Pipelines;

/// <summary>
/// Finds faces and their five landmarks with YuNet (PB30), the detector GFPGAN's crops are
/// aligned from. The photo is scaled to fit the model's fixed square, top-left, and the heads
/// are decoded as OpenCV's FaceDetectorYN does, then overlapping boxes are suppressed.
/// </summary>
public static class FaceDetection
{
    /// <summary>The detector's folder name.</summary>
    public const string Model = "yunet-2023mar";

    /// <summary>The lowest score kept: OpenCV's own sample default.</summary>
    public const double DefaultThreshold = 0.9;

    /// <summary>The overlap above which the weaker of two boxes is dropped: OpenCV's default.</summary>
    public const double NmsOverlap = 0.3;

    /// <summary>Finds the faces in a photo; call it from the inference queue.</summary>
    /// <param name="library">The models and their sessions.</param>
    /// <param name="image">The photo.</param>
    /// <param name="threshold">The lowest score kept.</param>
    /// <returns>The faces in photo coordinates, most confident first.</returns>
    public static IReadOnlyList<Face> Run(ModelLibrary library, RgbaImage image, double threshold = DefaultThreshold)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(image);
        ModelMeta meta = library.Meta(Model);
        if (meta.Output is not { Decoder: "yunet", Strides: { Count: > 0 } strides } || meta.Input.Size is not int side)
        {
            throw new InvalidDataException($"{Model}'s meta.json does not describe a fixed-size YuNet detector.");
        }

        double scale = Math.Min(side / (double)image.Width, side / (double)image.Height);
        float[] input = Letterbox(image, side, scale, meta.Input);
        Dictionary<string, float[]> heads = new(StringComparer.Ordinal);
        string[] names = [.. strides.SelectMany(s => new[] { $"cls_{s}", $"obj_{s}", $"bbox_{s}", $"kps_{s}" })];
        using (SessionLease<InferenceSession> lease = library.Sessions.Acquire(new ModelKey(Model, "fp32")))
        {
            using OrtValue tensor = OrtValue.CreateTensorValueFromMemory(input, [1, 3, side, side]);
            using RunOptions run = new();
            using IDisposableReadOnlyCollection<OrtValue> outputs = lease.Session.Run(run, [meta.Input.Name], [tensor], names);
            for (int i = 0; i < names.Length; i++)
            {
                heads[names[i]] = outputs[i].GetTensorDataAsSpan<float>().ToArray();
            }
        }

        return [.. Suppress(Decode(name => heads[name], side, strides, threshold), NmsOverlap).Select(face => Unscale(face, scale, image))];
    }

    /// <summary>
    /// YuNet's heads decoded as OpenCV's FaceDetectorYN does: for each stride, anchor (row, col)
    /// scores sqrt(cls x obj), its box centre is (col + dx, row + dy) x stride, its size exp(w, h) x
    /// stride, and each landmark (col + lx, row + ly) x stride.
    /// </summary>
    /// <param name="head">The flat values of a head by name, e.g. cls_8.</param>
    /// <param name="side">The model's input side.</param>
    /// <param name="strides">The head strides.</param>
    /// <param name="threshold">The lowest score kept.</param>
    /// <returns>Every anchor over the threshold, in input coordinates, unsuppressed.</returns>
    public static List<Face> Decode(Func<string, float[]> head, int side, IReadOnlyList<int> strides, double threshold)
    {
        ArgumentNullException.ThrowIfNull(head);
        ArgumentNullException.ThrowIfNull(strides);
        List<Face> faces = [];
        foreach (int stride in strides)
        {
            int cols = side / stride;
            float[] cls = head($"cls_{stride}");
            float[] obj = head($"obj_{stride}");
            float[] box = head($"bbox_{stride}");
            float[] kps = head($"kps_{stride}");
            for (int i = 0; i < cls.Length; i++)
            {
                double score = Math.Sqrt(Math.Clamp(cls[i], 0f, 1f) * Math.Clamp(obj[i], 0f, 1f));
                if (score < threshold)
                {
                    continue;
                }

                int row = i / cols;
                int col = i % cols;
                double width = Math.Exp(box[(i * 4) + 2]) * stride;
                double height = Math.Exp(box[(i * 4) + 3]) * stride;
                double cx = (col + box[i * 4]) * stride;
                double cy = (row + box[(i * 4) + 1]) * stride;
                PointD[] marks = new PointD[5];
                for (int k = 0; k < 5; k++)
                {
                    marks[k] = new((col + kps[(i * 10) + (2 * k)]) * stride, (row + kps[(i * 10) + (2 * k) + 1]) * stride);
                }

                faces.Add(new(cx - (width / 2), cy - (height / 2), width, height, score, marks));
            }
        }

        return faces;
    }

    /// <summary>Greedy non-maximum suppression: the best box stays, and any it overlaps too much goes.</summary>
    /// <param name="faces">The candidates.</param>
    /// <param name="overlap">The largest intersection over union two kept boxes may have.</param>
    /// <returns>The kept faces, most confident first.</returns>
    public static List<Face> Suppress(IEnumerable<Face> faces, double overlap)
    {
        List<Face> kept = [];
        foreach (Face face in faces.OrderByDescending(f => f.Score))
        {
            if (kept.TrueForAll(k => Overlap(k, face) <= overlap))
            {
                kept.Add(face);
            }
        }

        return kept;
    }

    private static double Overlap(Face a, Face b)
    {
        double w = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
        double h = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
        if (w <= 0 || h <= 0)
        {
            return 0;
        }

        double both = w * h;
        return both / ((a.Width * a.Height) + (b.Width * b.Height) - both);
    }

    private static Face Unscale(Face face, double scale, RgbaImage image)
    {
        double left = Math.Clamp(face.X / scale, 0, image.Width);
        double top = Math.Clamp(face.Y / scale, 0, image.Height);
        double right = Math.Clamp((face.X + face.Width) / scale, 0, image.Width);
        double bottom = Math.Clamp((face.Y + face.Height) / scale, 0, image.Height);
        return face with
        {
            X = left,
            Y = top,
            Width = right - left,
            Height = bottom - top,
            Landmarks = [.. face.Landmarks.Select(p => new PointD(p.X / scale, p.Y / scale))],
        };
    }

    // The photo scaled by `scale` into the top-left of a side x side NCHW tensor, zero beyond it.
    // Shrinking averages each output pixel's footprint, since one bilinear tap per pixel would
    // alias a large photo into noise; growing repeats the nearest pixel, which a detector minds less.
    private static float[] Letterbox(RgbaImage image, int side, double scale, ModelInput input)
    {
        int width = Math.Clamp((int)Math.Round(image.Width * scale), 1, side);
        int height = Math.Clamp((int)Math.Round(image.Height * scale), 1, side);
        float low = (float)input.ValueRange[0];
        float span = (float)(input.ValueRange[1] - input.ValueRange[0]);
        bool bgr = input.ColourOrder == "BGR";
        float[] tensor = new float[3 * side * side];
        if (low != 0)
        {
            Array.Fill(tensor, low);
        }

        Span<double> sum = stackalloc double[3];
        for (int y = 0; y < height; y++)
        {
            int y0 = Math.Min((int)(y / scale), image.Height - 1);
            int y1 = Math.Clamp((int)Math.Ceiling((y + 1) / scale), y0 + 1, image.Height);
            for (int x = 0; x < width; x++)
            {
                int x0 = Math.Min((int)(x / scale), image.Width - 1);
                int x1 = Math.Clamp((int)Math.Ceiling((x + 1) / scale), x0 + 1, image.Width);
                Average(image, new(x0, y0, x1 - x0, y1 - y0), sum);
                for (int c = 0; c < 3; c++)
                {
                    int plane = bgr ? 2 - c : c;
                    tensor[(plane * side * side) + (y * side) + x] = low + (span * (float)sum[c]);
                }
            }
        }

        return tensor;
    }

    // The mean red, green and blue over a rectangle, each 0 to 1.
    private static void Average(RgbaImage image, PixelRect area, Span<double> rgb)
    {
        rgb.Clear();
        for (int y = area.Y; y < area.Y + area.Height; y++)
        {
            ReadOnlySpan<ushort> row = image.Row(y).Slice(area.X * 4, area.Width * 4);
            for (int i = 0; i < row.Length; i += 4)
            {
                rgb[0] += row[i];
                rgb[1] += row[i + 1];
                rgb[2] += row[i + 2];
            }
        }

        double count = area.Area * (double)RgbaImage.Max;
        for (int c = 0; c < 3; c++)
        {
            rgb[c] /= count;
        }
    }
}
