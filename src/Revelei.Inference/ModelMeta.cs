using System.Text.Json;
using System.Text.Json.Serialization;

namespace Revelei.Inference;

/// <summary>
/// One model's meta.json, as scripts/export-and-quantize.py writes it beside the ONNX variants
/// (PB11). scripts/meta.schema.json is the contract both sides are tested against; a field that
/// changes meaning bumps <see cref="Schema"/>, which <see cref="Load"/> refuses when unknown.
/// </summary>
/// <param name="Schema">The meta.json format version.</param>
/// <param name="Name">The model's name, e.g. realesrgan-x4plus.</param>
/// <param name="Task">upscale, face-restore, background-removal or face-detection.</param>
/// <param name="Scale">How much larger the output is than the input.</param>
/// <param name="Input">What the model takes.</param>
/// <param name="Output">What the model returns.</param>
/// <param name="Tile">How to size tiles for it.</param>
/// <param name="Variants">The ONNX files, by variant name (fp32, fp16, int8).</param>
/// <param name="Upstream">Where the weights came from and under which license.</param>
public sealed record ModelMeta(
    int Schema,
    string Name,
    string Task,
    int Scale,
    ModelInput Input,
    ModelOutput Output,
    ModelTile Tile,
    IReadOnlyDictionary<string, ModelVariant> Variants,
    ModelUpstream Upstream)
{
    /// <summary>The only meta.json format this build reads.</summary>
    public const int SupportedSchema = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    /// <summary>Reads and checks a meta.json file.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The model's description.</returns>
    /// <exception cref="InvalidDataException">The file is not a meta.json this build can trust.</exception>
    public static ModelMeta Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        ModelMeta meta;
        try
        {
            meta = JsonSerializer.Deserialize<ModelMeta>(stream, Options)
                   ?? throw new InvalidDataException($"{path} is empty.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"{path} is not a valid meta.json: {error.Message}", error);
        }

        if (meta.Schema != SupportedSchema)
        {
            throw new InvalidDataException($"{path} is meta.json schema {meta.Schema}; this build reads {SupportedSchema}.");
        }

        if (!meta.Variants.ContainsKey("fp32"))
        {
            throw new InvalidDataException($"{path} lists no fp32 variant.");
        }

        if (meta.Input.ValueRange.Count != 2 || meta.Output.ValueRange.Count != 2)
        {
            throw new InvalidDataException($"{path} has a value range that is not [low, high].");
        }

        return meta;
    }
}

/// <summary>The model's input tensor.</summary>
/// <param name="Name">The ONNX input name.</param>
/// <param name="Layout">Always NCHW.</param>
/// <param name="ColourOrder">RGB, or BGR for the face detector (PB30).</param>
/// <param name="Channels">3, or 4 for a matting model whose fourth channel is the trimap (PB48).</param>
/// <param name="ValueRange">[low, high] that pixel 0 and 255 map to.</param>
/// <param name="Size">A fixed square side, or null when height and width are free.</param>
/// <param name="Multiple">Height and width must be a multiple of this.</param>
/// <param name="Batch">A fixed batch size, or null when the batch is dynamic.</param>
public sealed record ModelInput(
    string Name,
    string Layout,
    string ColourOrder,
    int Channels,
    IReadOnlyList<double> ValueRange,
    int? Size,
    int Multiple,
    [property: JsonConverter(typeof(BatchConverter))] int? Batch);

/// <summary>The model's output tensor, or a detector's heads.</summary>
/// <param name="Name">The ONNX output name; with a decoder, the family whose heads are &lt;part&gt;_&lt;stride&gt;.</param>
/// <param name="Channels">3 for an image, 1 for a mask; with a decoder, the values per anchor.</param>
/// <param name="ValueRange">[low, high] of the values it returns.</param>
/// <param name="Decoder">How a detector's heads are read (PB30: "yunet"); null for an image or a mask.</param>
/// <param name="Strides">The detector's head strides, with <paramref name="Decoder"/>.</param>
public sealed record ModelOutput(string Name, int Channels, IReadOnlyList<double> ValueRange, string? Decoder = null, IReadOnlyList<int>? Strides = null);

/// <summary>Tiling hints.</summary>
/// <param name="Recommended">The tile side to start from, or null for a fixed-size model.</param>
/// <param name="LargestActivationBytesPerPixel">The biggest FP32 tensor any layer makes, per input pixel; not the peak.</param>
public sealed record ModelTile(int? Recommended, double LargestActivationBytesPerPixel);

/// <summary>One ONNX file.</summary>
/// <param name="File">Its name in the model's folder.</param>
/// <param name="Bytes">Its size.</param>
/// <param name="Sha256">Its SHA-256, lowercase hex, which a download is checked against.</param>
public sealed record ModelVariant(string File, long Bytes, string Sha256);

/// <summary>Where the weights came from.</summary>
/// <param name="Url">The upstream weights.</param>
/// <param name="License">Their license, as the About page shows it.</param>
/// <param name="LicenseUrl">The license text.</param>
public sealed record ModelUpstream(string Url, string License, string LicenseUrl);

/// <summary>
/// Reads "batch": "dynamic" as null and a number as itself. Public only because the analyzer
/// does not count a type built through [JsonConverter] as used.
/// </summary>
public sealed class BatchConverter : JsonConverter<int?>
{
    /// <inheritdoc/>
    public override bool HandleNull => true;

    /// <inheritdoc/>
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetInt32(),
            JsonTokenType.String when reader.ValueTextEquals("dynamic"u8) => null,
            _ => throw new JsonException("batch must be \"dynamic\" or a number."),
        };

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value is int fixedBatch)
        {
            writer.WriteNumberValue(fixedBatch);
        }
        else
        {
            writer.WriteStringValue("dynamic");
        }
    }
}
