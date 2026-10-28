using System.Text.Json;
using System.Text.Json.Serialization;

namespace Revelei.Generation;

/// <summary>One image to generate (PB78). The files are the catalog's, already on disk.</summary>
/// <param name="DiffusionModel">The diffusion model's GGUF or safetensors.</param>
/// <param name="TextEncoder">The text encoder (an LLM such as Qwen3 4B).</param>
/// <param name="Vae">The VAE.</param>
/// <param name="Prompt">What to draw.</param>
/// <param name="Width">Columns, a multiple of 16.</param>
/// <param name="Height">Rows, a multiple of 16.</param>
/// <param name="Steps">Sampling steps.</param>
/// <param name="Guidance">The CFG scale; 1 for distilled models that take none.</param>
/// <param name="Seed">The seed, which with the rest makes the image reproducible.</param>
/// <param name="Offload">Keep the weights in RAM and stream them, for a model larger than video memory.</param>
/// <param name="NegativePrompt">What to avoid, for a model that uses one.</param>
public sealed record GenerationRequest(
    string DiffusionModel,
    string TextEncoder,
    string Vae,
    string Prompt,
    int Width = 1024,
    int Height = 1024,
    int Steps = 8,
    float Guidance = 1,
    long Seed = 42,
    bool Offload = false,
    string? NegativePrompt = null);

/// <summary>What a generation is doing.</summary>
public enum GenerationStage
{
    /// <summary>Reading the weights and placing them on the GPU or in RAM.</summary>
    Loading,

    /// <summary>Running the sampling steps.</summary>
    Sampling,

    /// <summary>Turning the latent into pixels.</summary>
    Decoding,
}

/// <summary>How far a generation is.</summary>
/// <param name="Stage">What it is doing.</param>
/// <param name="Step">The step done within the stage.</param>
/// <param name="Steps">The steps the stage takes.</param>
public readonly record struct GenerationProgress(GenerationStage Stage, int Step, int Steps);

/// <summary>A generation that failed, with the engine's own words.</summary>
public sealed class GenerationFailedException : Exception
{
    /// <summary>A failure.</summary>
    public GenerationFailedException()
    {
    }

    /// <summary>A failure, said.</summary>
    /// <param name="message">What went wrong.</param>
    public GenerationFailedException(string message)
        : base(message)
    {
    }

    /// <summary>A failure, said, and what caused it.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="inner">The cause.</param>
    public GenerationFailedException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// One line of the protocol between <see cref="GenerationClient"/> and the host, as JSON:
/// generate and cancel go to the host on its standard input; progress, done and failed come
/// back on its standard output. The pixels travel in a file, never in the pipe.
/// </summary>
/// <param name="Type">generate, cancel, progress, preview, done or failed.</param>
/// <param name="Request">With generate: what to draw.</param>
/// <param name="Output">With generate: where to write the RGBA8 pixels; with done and preview, where they are.</param>
/// <param name="Progress">With progress: how far it is.</param>
/// <param name="Width">With done and preview: the image's columns.</param>
/// <param name="Height">With done and preview: the image's rows.</param>
/// <param name="Message">With failed: the engine's words.</param>
/// <param name="Cancelled">With failed: whether a cancel ended it.</param>
internal sealed record HostMessage(
    string Type,
    GenerationRequest? Request = null,
    string? Output = null,
    GenerationProgress? Progress = null,
    int Width = 0,
    int Height = 0,
    string? Message = null,
    bool Cancelled = false)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>The line, without its end.</summary>
    /// <returns>JSON.</returns>
    public string ToLine() => JsonSerializer.Serialize(this, Options);

    /// <summary>Reads a line, or null for one that is not a message (a native library's own print).</summary>
    /// <param name="line">The line.</param>
    /// <returns>The message.</returns>
    public static HostMessage? Parse(string line)
    {
        if (!line.StartsWith('{'))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<HostMessage>(line, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
