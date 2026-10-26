using System.Buffers.Binary;
using System.Text;

namespace Revelei.Inference;

/// <summary>
/// PB14: the probe a provider must run before it is chosen. A Real-ESRGAN-shaped ONNX model written
/// as protobuf bytes (from the PB5 spike), so detection needs no model file and
/// no download: Conv 3x3 (3 to 8 channels), LeakyRelu 0.2, nearest Resize x2, Conv 3x3 (8 to 3).
/// Those are the ops the upscaler spends its time in. Height and width are dynamic, as tiling needs.
/// </summary>
internal static class ProbeModel
{
    private const int Float = 1;

    public static byte[] Build()
    {
        Proto graph = new();
        graph.Message(1, Node("Conv", ["x", "w1", "b1"], "c1", Ints("kernel_shape", 3, 3), Ints("pads", 1, 1, 1, 1)));
        graph.Message(1, Node("LeakyRelu", ["c1"], "r1", FloatAttribute("alpha", 0.2f)));
        graph.Message(1, Node("Resize", ["r1", "", "scales"], "u1", StringAttribute("mode", "nearest")));
        graph.Message(1, Node("Conv", ["u1", "w2", "b2"], "y", Ints("kernel_shape", 3, 3), Ints("pads", 1, 1, 1, 1)));
        graph.String(2, "tiny-upscale");
        graph.Message(5, Tensor("w1", Weights(8 * 3 * 3 * 3, 1), 8, 3, 3, 3));
        graph.Message(5, Tensor("b1", Weights(8, 2), 8));
        graph.Message(5, Tensor("w2", Weights(3 * 8 * 3 * 3, 3), 3, 8, 3, 3));
        graph.Message(5, Tensor("b2", Weights(3, 4), 3));
        graph.Message(5, Tensor("scales", [1f, 1f, 2f, 2f], 4));
        graph.Message(11, Value("x", "batch", 3, "height", "width"));
        graph.Message(12, Value("y", "batch", 3, "height2", "width2"));

        Proto opset = new();
        opset.String(1, string.Empty);
        opset.Int(2, 13);

        Proto model = new();
        model.Int(1, 8);
        model.String(2, "revelei-spike");
        model.Message(7, graph);
        model.Message(8, opset);
        return model.ToArray();
    }

    /// <summary>Deterministic weights in [-0.25, 0.25), so every provider sees the same model.</summary>
    private static float[] Weights(int count, int seed)
    {
        float[] values = new float[count];
        uint state = (uint)seed * 2654435761u;
        for (int i = 0; i < count; i++)
        {
            state = (state * 1664525u) + 1013904223u;
            values[i] = ((state >> 8) / (float)(1 << 24) - 0.5f) * 0.5f;
        }

        return values;
    }

    private static Proto Node(string op, string[] inputs, string output, params Proto[] attributes)
    {
        Proto node = new();
        foreach (string input in inputs)
        {
            node.String(1, input);
        }

        node.String(2, output);
        node.String(3, output + "_" + op);
        node.String(4, op);
        foreach (Proto attribute in attributes)
        {
            node.Message(5, attribute);
        }

        return node;
    }

    private static Proto Ints(string name, params long[] values)
    {
        Proto attribute = new();
        attribute.String(1, name);
        foreach (long value in values)
        {
            attribute.Int(8, value);
        }

        attribute.Int(20, 7);
        return attribute;
    }

    private static Proto FloatAttribute(string name, float value)
    {
        Proto attribute = new();
        attribute.String(1, name);
        attribute.Fixed32(2, value);
        attribute.Int(20, 1);
        return attribute;
    }

    private static Proto StringAttribute(string name, string value)
    {
        Proto attribute = new();
        attribute.String(1, name);
        attribute.String(4, value);
        attribute.Int(20, 3);
        return attribute;
    }

    private static Proto Tensor(string name, float[] values, params long[] dims)
    {
        Proto tensor = new();
        foreach (long dim in dims)
        {
            tensor.Int(1, dim);
        }

        tensor.Int(2, Float);
        tensor.String(8, name);
        byte[] raw = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(i * 4), values[i]);
        }

        tensor.Bytes(9, raw);
        return tensor;
    }

    private static Proto Value(string name, string batch, long channels, string height, string width)
    {
        Proto shape = new();
        shape.Message(1, Dim(batch));
        Proto fixedDim = new();
        fixedDim.Int(1, channels);
        shape.Message(1, fixedDim);
        shape.Message(1, Dim(height));
        shape.Message(1, Dim(width));

        Proto tensorType = new();
        tensorType.Int(1, Float);
        tensorType.Message(2, shape);

        Proto type = new();
        type.Message(1, tensorType);

        Proto value = new();
        value.String(1, name);
        value.Message(2, type);
        return value;
    }

    private static Proto Dim(string parameter)
    {
        Proto dim = new();
        dim.String(2, parameter);
        return dim;
    }

    /// <summary>The four protobuf wire forms an ONNX model needs, and nothing else.</summary>
    private sealed class Proto
    {
        private readonly MemoryStream buffer = new();

        public void Int(int field, long value)
        {
            Tag(field, 0);
            Varint((ulong)value);
        }

        public void Fixed32(int field, float value)
        {
            Tag(field, 5);
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
            buffer.Write(bytes);
        }

        public void String(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));

        public void Message(int field, Proto value) => Bytes(field, value.ToArray());

        public void Bytes(int field, byte[] value)
        {
            Tag(field, 2);
            Varint((ulong)value.Length);
            buffer.Write(value);
        }

        public byte[] ToArray() => buffer.ToArray();

        private void Tag(int field, int wire) => Varint(((ulong)field << 3) | (uint)wire);

        private void Varint(ulong value)
        {
            while (value >= 0x80)
            {
                buffer.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }

            buffer.WriteByte((byte)value);
        }
    }
}
